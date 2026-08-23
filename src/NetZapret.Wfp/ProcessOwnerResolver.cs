using System.Runtime.InteropServices;
using NetZapret.Core.Connections;
using NetZapret.Wfp.Interop;

namespace NetZapret.Wfp;

/// <summary>
/// Восстанавливает PID процесса-владельца по локальному порту соединения.
/// </summary>
/// <remarks>
/// <para>
/// FWPM_NET_EVENT_CLASSIFY_ALLOW0 не содержит PID — только appId с путём к
/// исполняемому файлу. Для правил этого достаточно (они и матчатся по имени exe),
/// но в логе PID полезен, чтобы отличить два экземпляра одного приложения.
/// </para>
/// <para>
/// Способ по своей природе гоночный: к моменту, когда событие доедет до нас,
/// сокет мог закрыться. Поэтому PID — best-effort, и <c>null</c> здесь штатная
/// ситуация, а не ошибка. Ключ — пара (протокол, локальный порт): совпадения
/// портов между разными адресами теоретически возможны, но для диагностики
/// этой точности достаточно.
/// </para>
/// </remarks>
public sealed class ProcessOwnerResolver
{
    private readonly TimeSpan _minRefreshInterval;
    private readonly object _sync = new();

    private Dictionary<int, int> _portToPid = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public ProcessOwnerResolver(TimeSpan? minRefreshInterval = null)
    {
        _minRefreshInterval = minRefreshInterval ?? TimeSpan.FromMilliseconds(400);
    }

    /// <summary>
    /// Ищет PID для локального порта. При промахе один раз обновляет таблицу
    /// и пробует снова — новое соединение почти наверняка появилось уже после
    /// последнего снимка.
    /// </summary>
    public int? Resolve(ProtocolKind protocol, ushort localPort, bool isIpV6)
    {
        if (localPort == 0)
            return null;

        int key = MakeKey(protocol, localPort, isIpV6);

        if (TryGet(key, out int pid))
            return pid;

        Refresh(force: true);

        return TryGet(key, out pid) ? pid : null;
    }

    private bool TryGet(int key, out int pid)
    {
        Refresh(force: false);

        lock (_sync)
            return _portToPid.TryGetValue(key, out pid);
    }

    private void Refresh(bool force)
    {
        lock (_sync)
        {
            if (!force && DateTime.UtcNow - _lastRefreshUtc < _minRefreshInterval)
                return;

            var map = new Dictionary<int, int>(capacity: 512);

            ReadTable(map, tcp: true, ipv6: false);
            ReadTable(map, tcp: true, ipv6: true);
            ReadTable(map, tcp: false, ipv6: false);
            ReadTable(map, tcp: false, ipv6: true);

            _portToPid = map;
            _lastRefreshUtc = DateTime.UtcNow;
        }
    }

    private static void ReadTable(Dictionary<int, int> map, bool tcp, bool ipv6)
    {
        uint family = ipv6 ? IpHlpApiNative.AF_INET6 : IpHlpApiNative.AF_INET;
        uint tableClass = tcp ? IpHlpApiNative.TCP_TABLE_OWNER_PID_ALL : IpHlpApiNative.UDP_TABLE_OWNER_PID;

        uint size = 0;
        uint result = tcp
            ? IpHlpApiNative.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, tableClass, 0)
            : IpHlpApiNative.GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, tableClass, 0);

        if (result != IpHlpApiNative.ERROR_INSUFFICIENT_BUFFER || size == 0)
            return;

        // Между двумя вызовами таблица могла вырасти; небольшой запас дешевле
        // повторного круга, а на ERROR_INSUFFICIENT_BUFFER мы просто пропустим
        // этот снимок и возьмём следующий.
        size += 4096;
        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            result = tcp
                ? IpHlpApiNative.GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0)
                : IpHlpApiNative.GetExtendedUdpTable(buffer, ref size, false, family, tableClass, 0);

            if (result != IpHlpApiNative.NO_ERROR)
                return;

            int count = Marshal.ReadInt32(buffer, ConnectionTableLayout.NumEntriesOffset);
            if (count <= 0)
                return;

            (int rowSize, int portOffset, int pidOffset) = (tcp, ipv6) switch
            {
                (true, false) => (ConnectionTableLayout.TcpRowSize, ConnectionTableLayout.TcpRowLocalPort, ConnectionTableLayout.TcpRowPid),
                (true, true) => (ConnectionTableLayout.Tcp6RowSize, ConnectionTableLayout.Tcp6RowLocalPort, ConnectionTableLayout.Tcp6RowPid),
                (false, false) => (ConnectionTableLayout.UdpRowSize, ConnectionTableLayout.UdpRowLocalPort, ConnectionTableLayout.UdpRowPid),
                (false, true) => (ConnectionTableLayout.Udp6RowSize, ConnectionTableLayout.Udp6RowLocalPort, ConnectionTableLayout.Udp6RowPid),
            };

            // Не доверяем count больше, чем размеру буфера: повреждённое значение
            // иначе увело бы чтение за его пределы.
            long maxRows = (size - ConnectionTableLayout.FirstRowOffset) / rowSize;
            if (count > maxRows)
                count = (int)maxRows;

            var protocol = tcp ? ProtocolKind.Tcp : ProtocolKind.Udp;

            for (int i = 0; i < count; i++)
            {
                int rowOffset = ConnectionTableLayout.FirstRowOffset + (i * rowSize);

                int rawPort = Marshal.ReadInt32(buffer, rowOffset + portOffset);
                int pid = Marshal.ReadInt32(buffer, rowOffset + pidOffset);

                // dwLocalPort: порт лежит в младших 16 битах в сетевом порядке байт.
                ushort port = (ushort)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
                if (port == 0)
                    continue;

                map[MakeKey(protocol, port, ipv6)] = pid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int MakeKey(ProtocolKind protocol, ushort port, bool isIpV6) =>
        ((int)protocol << 20) | (isIpV6 ? 1 << 17 : 0) | port;
}
