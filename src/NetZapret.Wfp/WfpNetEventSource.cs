using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Channels;
using NetZapret.Core.Connections;
using NetZapret.Wfp.Interop;

namespace NetZapret.Wfp;

/// <summary>
/// Настройки наблюдателя WFP.
/// </summary>
public sealed class WfpNetEventSourceOptions
{
    /// <summary>
    /// Показывать только исходящие соединения. Входящие диспетчеру неинтересны:
    /// маршрутизировать в прокси можно лишь то, что инициировали мы.
    /// </summary>
    public bool OutboundOnly { get; init; } = true;

    /// <summary>Пропускать loopback — иначе лог тонет в локальном IPC.</summary>
    public bool SkipLoopback { get; init; } = true;

    /// <summary>Оставлять только TCP и UDP.</summary>
    public bool TcpUdpOnly { get; init; } = true;

    /// <summary>
    /// Ёмкость буфера между колбэком WFP и читателем. При переполнении
    /// выбрасываются самые старые события: отставший потребитель не должен
    /// тормозить сетевой стек.
    /// </summary>
    public int BufferCapacity { get; init; } = 8192;

    /// <summary>Печатать шестнадцатеричный дамп первых событий (проверка смещений).</summary>
    public int RawDumpCount { get; init; }
}

/// <summary>
/// Наблюдает за классификациями WFP через подписку на net-events.
/// </summary>
/// <remarks>
/// <para>
/// Источник строго пассивный: он не добавляет фильтров и не меняет судьбу
/// ни одного пакета. Это принципиально для сосуществования с Zapret — winws2
/// работает на своём WinDivert-слое, и мы не должны появляться на его пути.
/// </para>
/// <para>
/// Побочный эффект, который приходится аккуратно откатывать: сбор net-events
/// включается глобально для всей машины через FwpmEngineSetOption0. Исходные
/// значения сохраняются при старте и восстанавливаются в
/// <see cref="DisposeAsync"/>, иначе система осталась бы собирать события
/// после выхода из программы.
/// </para>
/// </remarks>
public sealed class WfpNetEventSource : IConnectionEventSource
{
    private readonly WfpNetEventSourceOptions _options;
    private readonly NtPathResolver _pathResolver;
    private readonly ProcessOwnerResolver _processResolver;
    private readonly Channel<ConnectionEvent> _channel;

    private IntPtr _engineHandle = IntPtr.Zero;
    private IntPtr _subscriptionHandle = IntPtr.Zero;

    // Делегат обязан пережить подписку: если его соберёт GC, ядро вызовет
    // освобождённый thunk.
    private FwpmNative.FwpmNetEventCallback1? _callback;

    private uint? _savedCollectNetEvents;
    private uint? _savedKeywords;

    // Счётчики по FWPM_NET_EVENT_TYPE. Нужны, чтобы «в таблице пусто» можно было
    // отличить от «событий нужного типа система не генерирует» — без этого
    // молчание источника ничего не сообщает.
    private readonly long[] _typeCounts = new long[16];

    private long _receivedCount;
    private long _droppedCount;
    private int _rawDumpsRemaining;
    private bool _disposed;

    public WfpNetEventSource(
        WfpNetEventSourceOptions? options = null,
        NtPathResolver? pathResolver = null,
        ProcessOwnerResolver? processResolver = null)
    {
        _options = options ?? new WfpNetEventSourceOptions();
        _pathResolver = pathResolver ?? new NtPathResolver();
        _processResolver = processResolver ?? new ProcessOwnerResolver();
        _rawDumpsRemaining = _options.RawDumpCount;

        _channel = Channel.CreateBounded<ConnectionEvent>(new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public string Name => "WFP net-events (FwpmNetEventSubscribe1)";

    /// <summary>Сколько событий пришло из ядра.</summary>
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <summary>Сколько событий выброшено из-за переполнения буфера.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Значение FWPM_ENGINE_COLLECT_NET_EVENTS, вычитанное обратно после установки.</summary>
    public uint? EffectiveCollectNetEvents { get; private set; }

    /// <summary>
    /// Значение FWPM_ENGINE_NET_EVENT_MATCH_ANY_KEYWORDS, вычитанное обратно.
    /// Читается именно после записи: движок может не принять запрошенные
    /// ключевые слова, и тогда молчаливое расхождение обнаружится сразу.
    /// </summary>
    public uint? EffectiveKeywords { get; private set; }

    /// <summary>Запрошено ли получение событий о разрешённых соединениях.</summary>
    public bool ClassifyAllowRequested =>
        EffectiveKeywords is { } keywords && (keywords & (uint)NetEventKeyword.ClassifyAllow) != 0;

    public long ClassifyAllowCount => Interlocked.Read(ref _typeCounts[(int)FwpmNetEventType.ClassifyAllow]);

    public long ClassifyDropCount => Interlocked.Read(ref _typeCounts[(int)FwpmNetEventType.ClassifyDrop]);

    /// <summary>Разбивка полученных событий по типам — для диагностики.</summary>
    public IReadOnlyList<(string Type, long Count)> EventTypeBreakdown()
    {
        var result = new List<(string, long)>();

        for (int i = 0; i < _typeCounts.Length; i++)
        {
            long count = Interlocked.Read(ref _typeCounts[i]);
            if (count > 0)
                result.Add((((FwpmNetEventType)i).ToString(), count));
        }

        return result;
    }

    /// <summary>Вызывается при каждом сыром дампе, если он включён.</summary>
    public Action<string>? RawDumpSink { get; set; }

    /// <summary>
    /// Открывает движок, включает сбор событий и подписывается.
    /// </summary>
    /// <exception cref="WfpException">Если WFP вернул ошибку.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_engineHandle != IntPtr.Zero)
            throw new InvalidOperationException("Источник уже запущен.");

        if (!IsElevated())
        {
            throw new WfpException(
                "Подписка на net-events WFP требует прав администратора. " +
                "Запустите netzapret из консоли с повышением прав.", 5);
        }

        uint status = FwpmNative.FwpmEngineOpen0(
            serverName: null,
            authnService: FwpmNative.RPC_C_AUTHN_WINNT,
            authIdentity: IntPtr.Zero,
            session: IntPtr.Zero,
            out _engineHandle);

        WfpException.ThrowIfFailed(status, "FwpmEngineOpen0");

        try
        {
            EnableEventCollection();
            Subscribe();
        }
        catch
        {
            // Не оставляем машину с включённым сбором событий, если упали на полпути.
            SafeShutdown();
            throw;
        }
    }

    private void EnableEventCollection()
    {
        _savedCollectNetEvents = GetOption(FwpmEngineOption.CollectNetEvents);
        _savedKeywords = GetOption(FwpmEngineOption.NetEventMatchAnyKeywords);

        SetOption(FwpmEngineOption.CollectNetEvents, 1);

        // Сохраняем чужие ключевые слова и добавляем своё: сбор событий —
        // общесистемная настройка, и её может использовать кто-то ещё.
        uint keywords = (_savedKeywords ?? 0) | (uint)NetEventKeyword.ClassifyAllow;
        SetOption(FwpmEngineOption.NetEventMatchAnyKeywords, keywords);

        EffectiveCollectNetEvents = GetOption(FwpmEngineOption.CollectNetEvents);
        EffectiveKeywords = GetOption(FwpmEngineOption.NetEventMatchAnyKeywords);
    }

    private void Subscribe()
    {
        var subscription = new FWPM_NET_EVENT_SUBSCRIPTION0
        {
            EnumTemplate = IntPtr.Zero,
            Flags = 0,
            SessionKey = Guid.Empty,
        };

        _callback = OnNetEvent;

        uint status = FwpmNative.FwpmNetEventSubscribe1(
            _engineHandle,
            ref subscription,
            _callback,
            IntPtr.Zero,
            out _subscriptionHandle);

        WfpException.ThrowIfFailed(status, "FwpmNetEventSubscribe1");
    }

    /// <summary>
    /// Колбэк из ядра. Выполняется на потоке RPC-пула, поэтому здесь только
    /// разбор и запись в канал — никакой обработки правил и никакого ввода-вывода.
    /// </summary>
    private void OnNetEvent(IntPtr context, IntPtr netEvent)
    {
        if (netEvent == IntPtr.Zero)
            return;

        try
        {
            if (_rawDumpsRemaining > 0 && Interlocked.Decrement(ref _rawDumpsRemaining) >= 0)
                RawDumpSink?.Invoke(NetEventReader.DumpRaw(netEvent));

            var raw = NetEventReader.Read(netEvent);

            int typeIndex = (int)raw.Type;
            if (typeIndex >= 0 && typeIndex < _typeCounts.Length)
                Interlocked.Increment(ref _typeCounts[typeIndex]);

            // Отброшенные соединения показываем наравне с разрешёнными: маршрутизировать
            // их некуда, но они подтверждают, что конвейер жив, и объясняют пустую таблицу.
            if (raw.Type is not (FwpmNetEventType.ClassifyAllow or FwpmNetEventType.ClassifyDrop))
                return;

            Interlocked.Increment(ref _receivedCount);

            var connection = Convert(raw);
            if (connection is null)
                return;

            if (!_channel.Writer.TryWrite(connection))
                Interlocked.Increment(ref _droppedCount);
        }
        catch (Exception)
        {
            // Исключение, выпущенное в неуправляемый вызов, роняет процесс.
            // Потеря одного события несопоставимо дешевле.
        }
    }

    private ConnectionEvent? Convert(RawNetEvent raw)
    {
        if (_options.OutboundOnly && raw.Direction != NetEventReader.DirectionOutbound)
            return null;

        bool loopback = raw.IsLoopback
            || (raw.RemoteAddress is not null && System.Net.IPAddress.IsLoopback(raw.RemoteAddress));

        if (_options.SkipLoopback && loopback)
            return null;

        var protocol = raw.IpProtocol switch
        {
            6 => ProtocolKind.Tcp,
            17 => ProtocolKind.Udp,
            1 => ProtocolKind.Icmp,
            58 => ProtocolKind.IcmpV6,
            _ => ProtocolKind.Unknown,
        };

        if (_options.TcpUdpOnly && protocol is not (ProtocolKind.Tcp or ProtocolKind.Udp))
            return null;

        bool isV6 = raw.RemoteAddress?.AddressFamily == AddressFamily.InterNetworkV6;
        var executablePath = _pathResolver.ToDosPath(raw.AppIdNtPath);

        int? pid = protocol is ProtocolKind.Tcp or ProtocolKind.Udp
            ? _processResolver.Resolve(protocol, raw.LocalPort, isV6)
            : null;

        return new ConnectionEvent
        {
            Timestamp = raw.Timestamp,
            Protocol = protocol,
            LocalAddress = raw.LocalAddress,
            LocalPort = raw.LocalPort,
            RemoteAddress = raw.RemoteAddress,
            RemotePort = raw.RemotePort,
            ExecutablePath = executablePath,
            ProcessId = pid,
            Direction = raw.Direction == NetEventReader.DirectionInbound
                ? ConnectionDirection.Inbound
                : ConnectionDirection.Outbound,
            IsLoopback = loopback,
            LayerId = raw.LayerId,
            Verdict = raw.Type == FwpmNetEventType.ClassifyAllow
                ? ObservedVerdict.Allowed
                : ObservedVerdict.Dropped,
        };
    }

    public IAsyncEnumerable<ConnectionEvent> ReadEventsAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    private uint? GetOption(FwpmEngineOption option)
    {
        uint status = FwpmNative.FwpmEngineGetOption0(_engineHandle, option, out var valuePtr);
        if (status != FwpmNative.ERROR_SUCCESS || valuePtr == IntPtr.Zero)
            return null;

        try
        {
            var value = Marshal.PtrToStructure<FWP_VALUE0>(valuePtr);
            return value.Type == FwpDataType.FWP_UINT32 ? value.UInt32 : null;
        }
        finally
        {
            FwpmNative.FwpmFreeMemory0(ref valuePtr);
        }
    }

    private void SetOption(FwpmEngineOption option, uint value)
    {
        var fwpValue = new FWP_VALUE0
        {
            Type = FwpDataType.FWP_UINT32,
            UInt32 = value,
        };

        uint status = FwpmNative.FwpmEngineSetOption0(_engineHandle, option, ref fwpValue);
        WfpException.ThrowIfFailed(status, $"FwpmEngineSetOption0({option})");
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        SafeShutdown();
        _channel.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    private void SafeShutdown()
    {
        if (_engineHandle == IntPtr.Zero)
            return;

        if (_subscriptionHandle != IntPtr.Zero)
        {
            FwpmNative.FwpmNetEventUnsubscribe0(_engineHandle, _subscriptionHandle);
            _subscriptionHandle = IntPtr.Zero;
        }

        // Порядок важен: сперва отписались, только потом откатываем настройки,
        // иначе останется окно, в котором колбэк ещё жив.
        TryRestore(FwpmEngineOption.NetEventMatchAnyKeywords, _savedKeywords);
        TryRestore(FwpmEngineOption.CollectNetEvents, _savedCollectNetEvents);

        FwpmNative.FwpmEngineClose0(_engineHandle);
        _engineHandle = IntPtr.Zero;
        _callback = null;
    }

    private void TryRestore(FwpmEngineOption option, uint? saved)
    {
        if (saved is null)
            return;

        try
        {
            SetOption(option, saved.Value);
        }
        catch (WfpException)
        {
            // Восстановление — это уборка; если не вышло, поднимать шум в Dispose незачем.
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Ошибка вызова WFP с кодом возврата.</summary>
public sealed class WfpException : Exception
{
    public WfpException(string message, uint errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }

    public uint ErrorCode { get; }

    internal static void ThrowIfFailed(uint status, string operation)
    {
        if (status == FwpmNative.ERROR_SUCCESS)
            return;

        throw new WfpException($"{operation} вернул 0x{status:X8}. {Describe(status)}", status);
    }

    private static string Describe(uint status) => status switch
    {
        5 => "Отказано в доступе — нужны права администратора.",
        0x80320003 => "FWP_E_NOT_FOUND.",
        0x80320009 => "FWP_E_TIMEOUT — движок BFE не отвечает.",
        0x80320035 => "FWP_E_NOT_READY — служба Base Filtering Engine не запущена.",
        _ => "Код см. в списке FWP_E_* / Win32.",
    };
}
