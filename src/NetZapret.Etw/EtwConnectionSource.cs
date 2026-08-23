using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using NetZapret.Core.Connections;

namespace NetZapret.Etw;

public sealed class EtwConnectionSourceOptions
{
    /// <summary>Пропускать loopback.</summary>
    public bool SkipLoopback { get; init; } = true;

    public int BufferCapacity { get; init; } = 8192;

    /// <summary>
    /// Окно подавления повторов для UDP. ETW сообщает о каждой датаграмме,
    /// поэтому один QUIC-поток иначе выдал бы тысячи одинаковых строк.
    /// </summary>
    public TimeSpan UdpDeduplicationWindow { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Наблюдать за ответами DNS, чтобы доменные правила можно было проверить
    /// без ручной карты имён.
    /// </summary>
    public bool ObserveDns { get; init; } = true;
}

/// <summary>
/// Источник соединений на основе ETW-провайдера ядра.
/// </summary>
/// <remarks>
/// <para>
/// Заменил WFP net-events в роли основного источника PoC по результатам проверки:
/// на Windows 11 26200 события FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW не приходят
/// даже при включённом сборе, принятом ключевом слове CLASSIFY_ALLOW и
/// включённой подкатегории аудита «Filtering Platform Connection» — событий 5156
/// в журнале безопасности при этом тоже нет. Приходят только CLASSIFY_DROP,
/// а по ним диспетчер маршрутизации построить нельзя.
/// </para>
/// <para>
/// Ядерный провайдер отдаёт ровно то, что нужно классификатору: PID, адрес и порт
/// назначения — и не требует общесистемных настроек, кроме прав администратора
/// на создание ETW-сессии.
/// </para>
/// </remarks>
public sealed class EtwConnectionSource : IConnectionEventSource
{
    private const string SessionName = "NetZapret-KernelNetwork";

    /// <summary>Microsoft-Windows-DNS-Client.</summary>
    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");

    /// <summary>DNS_QUERY_COMPLETE — запрос завершён, в полезной нагрузке есть адреса.</summary>
    private const int DnsQueryCompleteEventId = 3008;

    private readonly EtwConnectionSourceOptions _options;
    private readonly ProcessImageResolver _imageResolver;
    private readonly Channel<ConnectionEvent> _channel;
    private readonly ConcurrentDictionary<string, DateTime> _recentUdp = new();
    private readonly DnsNameCache _dnsNames = new();

    private TraceEventSession? _session;
    private Thread? _pumpThread;
    private long _receivedCount;
    private long _droppedCount;
    private long _suppressedUdpCount;
    private DateTime _lastPruneUtc = DateTime.UtcNow;
    private bool _disposed;

    public EtwConnectionSource(
        EtwConnectionSourceOptions? options = null,
        ProcessImageResolver? imageResolver = null)
    {
        _options = options ?? new EtwConnectionSourceOptions();
        _imageResolver = imageResolver ?? new ProcessImageResolver();

        _channel = Channel.CreateBounded<ConnectionEvent>(new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public string Name => "ETW Microsoft-Windows-Kernel-Network";

    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Сколько повторных UDP-датаграмм подавлено дедупликацией.</summary>
    public long SuppressedUdpCount => Interlocked.Read(ref _suppressedUdpCount);

    /// <summary>
    /// Имена, подсмотренные в ответах DNS. Пусто, если наблюдение отключено.
    /// </summary>
    public DnsNameCache DnsNames => _dnsNames;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_session is not null)
            throw new InvalidOperationException("Источник уже запущен.");

        StopStaleSession();

        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var kernel = _session.Source.Kernel;
        kernel.TcpIpConnect += OnTcpConnect;
        kernel.TcpIpConnectIPV6 += OnTcpConnectV6;
        kernel.UdpIpSend += OnUdpSend;
        kernel.UdpIpSendIPV6 += OnUdpSendV6;

        if (_options.ObserveDns)
        {
            // DNS-клиент — обычный user-mode провайдер, его можно поднять
            // в той же сессии, что и ядерный. Одна сессия проще в уборке,
            // и порядок событий между провайдерами сохраняется, а он важен:
            // ответ DNS должен успеть записаться до события о соединении.
            _session.EnableProvider(DnsClientProvider);
            _session.Source.Dynamic.All += OnDynamicEvent;
        }

        // Process() блокирует до остановки сессии, поэтому живёт на своём потоке.
        _pumpThread = new Thread(PumpEvents)
        {
            IsBackground = true,
            Name = "netzapret-etw",
        };

        _pumpThread.Start();
    }

    /// <summary>
    /// Останавливает сессию, оставшуюся от прерванного запуска.
    /// </summary>
    /// <remarks>
    /// ETW-сессии переживают завершение создавшего их процесса. Если предыдущий
    /// запуск убили, сессия с этим именем всё ещё существует, и создание новой
    /// упало бы с «уже существует».
    /// </remarks>
    private static void StopStaleSession()
    {
        try
        {
            if (!TraceEventSession.GetActiveSessionNames().Contains(SessionName))
                return;

            using var stale = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
            stale.Stop();
        }
        catch (Exception)
        {
            // Если убрать не вышло, создание новой сессии всё равно сообщит об этом внятнее.
        }
    }

    private void OnDynamicEvent(TraceEvent data)
    {
        if (data.ProviderGuid != DnsClientProvider || (int)data.ID != DnsQueryCompleteEventId)
            return;

        try
        {
            if (data.PayloadByName("QueryName") is not string queryName || queryName.Length == 0)
                return;

            var results = data.PayloadByName("QueryResults") as string;

            foreach (var address in DnsNameCache.ParseQueryResults(results))
                _dnsNames.Record(address, queryName);
        }
        catch (Exception)
        {
            // Полезная нагрузка события может отличаться между версиями Windows;
            // потеря одного имени не повод ронять сессию.
        }
    }

    private void PumpEvents()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception) when (_disposed)
        {
            // Остановка сессии на выходе прерывает Process() — это штатно.
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private void OnTcpConnect(TcpIpConnectTraceData data) =>
        Publish(ProtocolKind.Tcp, data.ProcessID, data.saddr, (ushort)data.sport, data.daddr, (ushort)data.dport);

    private void OnTcpConnectV6(TcpIpV6ConnectTraceData data) =>
        Publish(ProtocolKind.Tcp, data.ProcessID, data.saddr, (ushort)data.sport, data.daddr, (ushort)data.dport);

    private void OnUdpSend(UdpIpTraceData data) =>
        Publish(ProtocolKind.Udp, data.ProcessID, data.saddr, (ushort)data.sport, data.daddr, (ushort)data.dport);

    private void OnUdpSendV6(UpdIpV6TraceData data) =>
        Publish(ProtocolKind.Udp, data.ProcessID, data.saddr, (ushort)data.sport, data.daddr, (ushort)data.dport);

    private void Publish(
        ProtocolKind protocol,
        int processId,
        IPAddress localAddress,
        ushort localPort,
        IPAddress remoteAddress,
        ushort remotePort)
    {
        try
        {
            bool loopback = IPAddress.IsLoopback(remoteAddress);
            if (_options.SkipLoopback && loopback)
                return;

            if (protocol == ProtocolKind.Udp && !ShouldReportUdp(processId, remoteAddress, remotePort))
            {
                Interlocked.Increment(ref _suppressedUdpCount);
                return;
            }

            Interlocked.Increment(ref _receivedCount);

            var connection = new ConnectionEvent
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = protocol,
                LocalAddress = localAddress,
                LocalPort = localPort,
                RemoteAddress = remoteAddress,
                RemotePort = remotePort,
                ExecutablePath = _imageResolver.Resolve(processId),
                ProcessId = processId,
                Hostname = _dnsNames.Resolve(remoteAddress),
                Direction = ConnectionDirection.Outbound,
                IsLoopback = loopback,
                Verdict = ObservedVerdict.Allowed,
            };

            if (!_channel.Writer.TryWrite(connection))
                Interlocked.Increment(ref _droppedCount);
        }
        catch (Exception)
        {
            // Колбэк ETW: исключение отсюда убило бы поток обработки сессии.
        }
    }

    private bool ShouldReportUdp(int processId, IPAddress remoteAddress, ushort remotePort)
    {
        var now = DateTime.UtcNow;
        var key = $"{processId}|{remoteAddress}|{remotePort}";

        if (_recentUdp.TryGetValue(key, out var seenAt) && now - seenAt < _options.UdpDeduplicationWindow)
            return false;

        _recentUdp[key] = now;

        if (now - _lastPruneUtc > _options.UdpDeduplicationWindow)
        {
            _lastPruneUtc = now;

            foreach (var (staleKey, staleTime) in _recentUdp)
            {
                if (now - staleTime >= _options.UdpDeduplicationWindow)
                    _recentUdp.TryRemove(staleKey, out _);
            }
        }

        return true;
    }

    public IAsyncEnumerable<ConnectionEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;

        // Останавливает ETW-сессию и разблокирует Process() на потоке-насосе.
        _session?.Dispose();
        _session = null;

        _pumpThread?.Join(TimeSpan.FromSeconds(3));
        _pumpThread = null;

        _channel.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }
}
