using System.Runtime.InteropServices;

namespace NetZapret.Wfp.Interop;

internal static class IpHlpApiNative
{
    private const string IpHlpApi = "iphlpapi.dll";

    internal const uint NO_ERROR = 0;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;

    internal const uint AF_INET = 2;
    internal const uint AF_INET6 = 23;

    /// <summary>TCP_TABLE_OWNER_PID_ALL.</summary>
    internal const uint TCP_TABLE_OWNER_PID_ALL = 5;

    /// <summary>UDP_TABLE_OWNER_PID.</summary>
    internal const uint UDP_TABLE_OWNER_PID = 1;

    [DllImport(IpHlpApi, ExactSpelling = true)]
    internal static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref uint pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);

    [DllImport(IpHlpApi, ExactSpelling = true)]
    internal static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref uint pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);
}

/// <summary>Смещения строк таблиц соединений (x64, но все поля — DWORD, так что раскладка та же).</summary>
internal static class ConnectionTableLayout
{
    internal const int NumEntriesOffset = 0;
    internal const int FirstRowOffset = 4;

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
    internal const int TcpRowSize = 24;
    internal const int TcpRowLocalPort = 8;
    internal const int TcpRowPid = 20;

    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort,
    //                       remoteAddr[16], remoteScopeId, remotePort, state, pid
    internal const int Tcp6RowSize = 56;
    internal const int Tcp6RowLocalPort = 20;
    internal const int Tcp6RowPid = 52;

    // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
    internal const int UdpRowSize = 12;
    internal const int UdpRowLocalPort = 4;
    internal const int UdpRowPid = 8;

    // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, pid
    internal const int Udp6RowSize = 28;
    internal const int Udp6RowLocalPort = 20;
    internal const int Udp6RowPid = 24;
}
