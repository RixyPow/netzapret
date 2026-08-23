using System.Runtime.InteropServices;

namespace NetZapret.Wfp.Interop;

/// <summary>
/// P/Invoke к user-mode части Windows Filtering Platform (fwpuclnt.dll).
/// </summary>
/// <remarks>
/// Здесь используется только наблюдательная часть WFP: открыть движок, включить
/// сбор net-events и подписаться на них. Ни одного фильтра мы не добавляем —
/// PoC обязан быть полностью пассивным.
/// </remarks>
internal static class FwpmNative
{
    private const string Fwpuclnt = "fwpuclnt.dll";

    internal const uint ERROR_SUCCESS = 0;
    internal const uint RPC_C_AUTHN_WINNT = 10;

    /// <summary>
    /// Версия подписки выбрана намеренно старой. FwpmNetEventSubscribe1 отдаёт
    /// FWPM_NET_EVENT2 с заголовком HEADER2, который короче и проще HEADER3
    /// из более новых версий, но уже содержит и appId, и тип CLASSIFY_ALLOW.
    /// Меньше полей — меньше шансов ошибиться в смещениях, а ничего сверх
    /// HEADER2 диспетчеру не нужно. Доступно начиная с Windows 8.
    /// </summary>
    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmNetEventSubscribe1(
        IntPtr engineHandle,
        ref FWPM_NET_EVENT_SUBSCRIPTION0 subscription,
        FwpmNetEventCallback1 callback,
        IntPtr context,
        out IntPtr eventsHandle);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmNetEventUnsubscribe0(IntPtr engineHandle, IntPtr eventsHandle);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService,
        IntPtr authIdentity,
        IntPtr session,
        out IntPtr engineHandle);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmEngineSetOption0(
        IntPtr engineHandle,
        FwpmEngineOption option,
        ref FWP_VALUE0 newValue);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmEngineGetOption0(
        IntPtr engineHandle,
        FwpmEngineOption option,
        out IntPtr value);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern void FwpmFreeMemory0(ref IntPtr p);

    /// <summary>
    /// FWPM_NET_EVENT_CALLBACK1: вызывается на потоке RPC-пула.
    /// Второй аргумент — указатель на FWPM_NET_EVENT2, валидный только внутри вызова.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FwpmNetEventCallback1(IntPtr context, IntPtr netEvent2);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_NET_EVENT_SUBSCRIPTION0
{
    /// <summary>FWPM_NET_EVENT_ENUM_TEMPLATE0* — NULL означает «все события».</summary>
    public IntPtr EnumTemplate;

    public uint Flags;

    public Guid SessionKey;
}

/// <summary>
/// FWP_VALUE0. На x64: тег в 0..4, затем 4 байта выравнивания, объединение в 8..16.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FWP_VALUE0
{
    [FieldOffset(0)] public FwpDataType Type;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public IntPtr Pointer;
}

internal enum FwpDataType : uint
{
    FWP_EMPTY = 0,
    FWP_UINT8 = 1,
    FWP_UINT16 = 2,
    FWP_UINT32 = 3,
    FWP_UINT64 = 4,
}

internal enum FwpmEngineOption : uint
{
    CollectNetEvents = 0,
    NetEventMatchAnyKeywords = 1,
    NameCache = 2,
    MonitorIpsecConnections = 3,
    PacketQueuing = 4,
}

/// <summary>Ключевые слова, разрешающие генерацию тех или иных net-events.</summary>
[Flags]
internal enum NetEventKeyword : uint
{
    None = 0,
    InboundMulticast = 0x00000001,
    InboundBroadcast = 0x00000002,
    CapabilityDrop = 0x00000004,
    CapabilityAllow = 0x00000008,

    /// <summary>Ради этого всё и затевалось: события об успешно разрешённых соединениях.</summary>
    ClassifyAllow = 0x00000010,

    PortScanningDrop = 0x00000020,
}

internal enum FwpmNetEventType : uint
{
    IkeExtMmFailure = 0,
    IkeExtQmFailure = 1,
    IkeExtEmFailure = 2,
    ClassifyDrop = 3,
    IpsecKernelDrop = 4,
    IpsecDospDrop = 5,
    ClassifyAllow = 6,
    CapabilityDrop = 7,
    CapabilityAllow = 8,
    ClassifyDropMac = 9,
    LpmPacketArrival = 10,
}

internal enum FwpIpVersion : uint
{
    V4 = 0,
    V6 = 1,
    None = 2,
}

/// <summary>Какие поля заголовка net-event заполнены.</summary>
[Flags]
internal enum NetEventHeaderFlags : uint
{
    IpProtocolSet = 0x00000001,
    LocalAddrSet = 0x00000002,
    RemoteAddrSet = 0x00000004,
    LocalPortSet = 0x00000008,
    RemotePortSet = 0x00000010,
    AppIdSet = 0x00000020,
    UserIdSet = 0x00000040,
    ScopeIdSet = 0x00000080,
    IpVersionSet = 0x00000100,
    ReauthReasonSet = 0x00000200,
    PackageIdSet = 0x00000400,
}
