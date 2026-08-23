using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace NetZapret.Wfp.Interop;

/// <summary>
/// Смещения полей FWPM_NET_EVENT2 / FWPM_NET_EVENT_HEADER2 для x64 ABI.
/// </summary>
/// <remarks>
/// <para>
/// Поля читаются вручную по смещениям, а не через <c>Marshal.PtrToStructure</c>
/// с описанием структуры. Причина: в заголовке два объединения по 16 байт
/// (FWP_V4_ADDR / FWP_BYTE_ARRAY16) с выравниванием 4, и добиться от маршалера
/// C#-раскладки, совпадающей с C, можно только явными полями-заполнителями.
/// Ошибка в такой структуре проявляется как молча неверные адреса, а не как
/// исключение. Явные именованные константы проще проверить и, если понадобится,
/// поправить в одном месте — см. <see cref="NetEventReader.DumpRaw"/> для
/// эмпирической сверки.
/// </para>
/// <para>
/// Раскладка FWPM_NET_EVENT_HEADER2 (x64, суммарно 104 байта):
/// <code>
///   0   FILETIME       timeStamp
///   8   UINT32         flags
///  12   FWP_IP_VERSION ipVersion
///  16   UINT8          ipProtocol      (+3 байта выравнивания)
///  20   union          localAddr       (FWP_V4_ADDR | FWP_BYTE_ARRAY16)
///  36   union          remoteAddr
///  52   UINT16         localPort
///  54   UINT16         remotePort
///  56   UINT32         scopeId         (+4 байта выравнивания)
///  64   FWP_BYTE_BLOB  appId           { UINT32 size; +4 pad; UINT8* data }
///  80   SID*           userId
///  88   FWP_AF         addressFamily   (+4 байта выравнивания)
///  96   SID*           packageSid
/// </code>
/// Далее FWPM_NET_EVENT2: тип в 104, выравнивание, указатель объединения в 112.
/// </para>
/// </remarks>
internal static class NetEventLayout
{
    internal const int HeaderTimeStamp = 0;
    internal const int HeaderFlags = 8;
    internal const int HeaderIpVersion = 12;
    internal const int HeaderIpProtocol = 16;
    internal const int HeaderLocalAddr = 20;
    internal const int HeaderRemoteAddr = 36;
    internal const int HeaderLocalPort = 52;
    internal const int HeaderRemotePort = 54;
    internal const int HeaderScopeId = 56;
    internal const int HeaderAppIdSize = 64;
    internal const int HeaderAppIdData = 72;
    internal const int HeaderUserId = 80;
    internal const int HeaderAddressFamily = 88;
    internal const int HeaderPackageSid = 96;

    internal const int HeaderSize = 104;

    internal const int EventType = HeaderSize;          // 104
    internal const int EventUnionPointer = HeaderSize + 8; // 112
    internal const int EventSize = 120;

    // Первые 32 байта FWPM_NET_EVENT_CLASSIFY_ALLOW0 и FWPM_NET_EVENT_CLASSIFY_DROP2
    // совпадают поле в поле; DROP2 лишь добавляет за ними данные vSwitch.
    // Поэтому оба типа событий разбираются одним набором смещений.
    internal const int VerdictFilterId = 0;
    internal const int VerdictLayerId = 8;
    internal const int VerdictReauthReason = 12;
    internal const int VerdictOriginalProfile = 16;
    internal const int VerdictCurrentProfile = 20;
    internal const int VerdictDirection = 24;
    internal const int VerdictIsLoopback = 28;
}

/// <summary>Разобранное событие WFP в удобном для дальнейшей работы виде.</summary>
internal sealed class RawNetEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public FwpmNetEventType Type { get; init; }
    public byte IpProtocol { get; init; }
    public IPAddress? LocalAddress { get; init; }
    public IPAddress? RemoteAddress { get; init; }
    public ushort LocalPort { get; init; }
    public ushort RemotePort { get; init; }
    public string? AppIdNtPath { get; init; }
    public ushort LayerId { get; init; }
    public bool IsLoopback { get; init; }
    public uint Direction { get; init; }
}

internal static class NetEventReader
{
    /// <summary>FWP_DIRECTION_OUTBOUND.</summary>
    internal const uint DirectionOutbound = 0;

    /// <summary>FWP_DIRECTION_INBOUND.</summary>
    internal const uint DirectionInbound = 1;

    /// <summary>
    /// Разбирает FWPM_NET_EVENT2 по указателю, валидному только на время колбэка.
    /// </summary>
    public static RawNetEvent Read(IntPtr netEvent)
    {
        var flags = (NetEventHeaderFlags)(uint)Marshal.ReadInt32(netEvent, NetEventLayout.HeaderFlags);
        var ipVersion = (FwpIpVersion)(uint)Marshal.ReadInt32(netEvent, NetEventLayout.HeaderIpVersion);
        var type = (FwpmNetEventType)(uint)Marshal.ReadInt32(netEvent, NetEventLayout.EventType);

        long fileTime = Marshal.ReadInt64(netEvent, NetEventLayout.HeaderTimeStamp);

        byte protocol = flags.HasFlag(NetEventHeaderFlags.IpProtocolSet)
            ? Marshal.ReadByte(netEvent, NetEventLayout.HeaderIpProtocol)
            : (byte)0;

        var local = flags.HasFlag(NetEventHeaderFlags.LocalAddrSet)
            ? ReadAddress(netEvent, NetEventLayout.HeaderLocalAddr, ipVersion)
            : null;

        var remote = flags.HasFlag(NetEventHeaderFlags.RemoteAddrSet)
            ? ReadAddress(netEvent, NetEventLayout.HeaderRemoteAddr, ipVersion)
            : null;

        // Порты в WFP хранятся в порядке байт хоста, разворачивать не нужно.
        ushort localPort = flags.HasFlag(NetEventHeaderFlags.LocalPortSet)
            ? (ushort)Marshal.ReadInt16(netEvent, NetEventLayout.HeaderLocalPort)
            : (ushort)0;

        ushort remotePort = flags.HasFlag(NetEventHeaderFlags.RemotePortSet)
            ? (ushort)Marshal.ReadInt16(netEvent, NetEventLayout.HeaderRemotePort)
            : (ushort)0;

        string? appId = flags.HasFlag(NetEventHeaderFlags.AppIdSet)
            ? ReadAppId(netEvent)
            : null;

        ushort layerId = 0;
        bool isLoopback = false;
        uint direction = DirectionOutbound;

        if (type is FwpmNetEventType.ClassifyAllow or FwpmNetEventType.ClassifyDrop)
        {
            var verdict = Marshal.ReadIntPtr(netEvent, NetEventLayout.EventUnionPointer);
            if (verdict != IntPtr.Zero)
            {
                layerId = (ushort)Marshal.ReadInt16(verdict, NetEventLayout.VerdictLayerId);
                direction = (uint)Marshal.ReadInt32(verdict, NetEventLayout.VerdictDirection);
                isLoopback = Marshal.ReadInt32(verdict, NetEventLayout.VerdictIsLoopback) != 0;
            }
        }

        return new RawNetEvent
        {
            Timestamp = SafeFromFileTime(fileTime),
            Type = type,
            IpProtocol = protocol,
            LocalAddress = local,
            RemoteAddress = remote,
            LocalPort = localPort,
            RemotePort = remotePort,
            AppIdNtPath = appId,
            LayerId = layerId,
            IsLoopback = isLoopback,
            Direction = direction,
        };
    }

    private static DateTimeOffset SafeFromFileTime(long fileTime)
    {
        // Ядро изредка отдаёт нули и заведомо некорректные значения; падать
        // из-за метки времени в наблюдателе трафика нет смысла.
        if (fileTime <= 0)
            return DateTimeOffset.UtcNow;

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static IPAddress? ReadAddress(IntPtr netEvent, int offset, FwpIpVersion version)
    {
        switch (version)
        {
            case FwpIpVersion.V4:
            {
                // FWP_V4_ADDR — UINT32 в порядке байт хоста (0xC0A80001 == 192.168.0.1).
                uint value = (uint)Marshal.ReadInt32(netEvent, offset);
                Span<byte> octets =
                [
                    (byte)(value >> 24),
                    (byte)(value >> 16),
                    (byte)(value >> 8),
                    (byte)value,
                ];
                return new IPAddress(octets);
            }

            case FwpIpVersion.V6:
            {
                // FWP_BYTE_ARRAY16 — уже в сетевом порядке байт.
                var bytes = new byte[16];
                Marshal.Copy(netEvent + offset, bytes, 0, 16);
                return new IPAddress(bytes);
            }

            default:
                return null;
        }
    }

    private static string? ReadAppId(IntPtr netEvent)
    {
        uint size = (uint)Marshal.ReadInt32(netEvent, NetEventLayout.HeaderAppIdSize);
        var data = Marshal.ReadIntPtr(netEvent, NetEventLayout.HeaderAppIdData);

        if (data == IntPtr.Zero || size == 0)
            return null;

        // Верхняя граница на случай, если смещение поля окажется неверным:
        // без неё ошибка в раскладке превращается в попытку прочитать гигабайты.
        if (size > 64 * 1024)
            return null;

        // appId — UTF-16 строка с завершающим нулём, размер задан в байтах.
        int charCount = (int)(size / sizeof(char));
        var text = Marshal.PtrToStringUni(data, charCount);
        return text?.TrimEnd('\0');
    }

    /// <summary>
    /// Шестнадцатеричный дамп начала события — для эмпирической проверки смещений,
    /// если раскладка вдруг разойдётся с ожидаемой.
    /// </summary>
    public static string DumpRaw(IntPtr netEvent, int length = NetEventLayout.EventSize)
    {
        var bytes = new byte[length];
        Marshal.Copy(netEvent, bytes, 0, length);

        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += 16)
        {
            sb.Append(i.ToString("D3")).Append(" | ");
            for (int j = 0; j < 16 && i + j < bytes.Length; j++)
                sb.Append(bytes[i + j].ToString("X2")).Append(' ');
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
