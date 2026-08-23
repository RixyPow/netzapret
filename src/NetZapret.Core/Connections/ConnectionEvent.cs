using System.Net;
using System.Net.Sockets;

namespace NetZapret.Core.Connections;

/// <summary>
/// Одно наблюдённое соединение — то, что видит классификатор в момент решения.
/// </summary>
/// <remarks>
/// Тип намеренно описывает соединение независимо от источника: сегодня его
/// наполняет WFP-подписка на net-events, завтра — собственный драйвер или
/// Clash-API sing-box. Правила не должны знать, откуда пришли данные.
/// </remarks>
public sealed record ConnectionEvent
{
    public required DateTimeOffset Timestamp { get; init; }

    public required ProtocolKind Protocol { get; init; }

    public IPAddress? LocalAddress { get; init; }

    public ushort LocalPort { get; init; }

    public IPAddress? RemoteAddress { get; init; }

    public ushort RemotePort { get; init; }

    /// <summary>
    /// Полный путь к исполняемому файлу в DOS-нотации (C:\...), если удалось
    /// преобразовать NT-путь из WFP appId.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Имя файла без каталога, в нижнем регистре.</summary>
    public string? ExecutableName =>
        ExecutablePath is null ? null : Path.GetFileName(ExecutablePath).ToLowerInvariant();

    /// <summary>
    /// PID процесса-владельца. WFP в CLASSIFY_ALLOW его не отдаёт, поэтому
    /// значение восстанавливается сопоставлением с таблицей соединений и
    /// может отсутствовать, если сокет уже закрылся.
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Доменное имя назначения, если известно. В PoC заполняется только из
    /// внешней карты (--hosts-map); в бою — из fakeip-таблицы sing-box и sniffing'а.
    /// </summary>
    public string? Hostname { get; init; }

    /// <summary>Направление относительно локальной машины.</summary>
    public ConnectionDirection Direction { get; init; } = ConnectionDirection.Unknown;

    /// <summary>Соединение на loopback — такие всегда остаются direct.</summary>
    public bool IsLoopback { get; init; }

    /// <summary>Идентификатор слоя WFP, на котором произошла классификация (диагностика).</summary>
    public ushort LayerId { get; init; }

    /// <summary>
    /// Что система на самом деле сделала с соединением.
    /// </summary>
    /// <remarks>
    /// Диспетчер интересует прежде всего разрешённый трафик — только его есть
    /// смысл куда-то направлять. Но отброшенные соединения тоже стоит показывать:
    /// это единственный способ отличить «правило не сработало» от «трафика не было».
    /// </remarks>
    public ObservedVerdict Verdict { get; init; } = ObservedVerdict.Unknown;

    public ConnectionEvent WithHostname(string? hostname) =>
        hostname is null ? this : this with { Hostname = hostname };

    public string DescribeEndpoint()
    {
        var remote = RemoteAddress is null
            ? "?"
            : RemoteAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{RemoteAddress}]"
                : RemoteAddress.ToString();

        return $"{remote}:{RemotePort}";
    }
}

public enum ProtocolKind
{
    Unknown = 0,
    Tcp = 6,
    Udp = 17,
    Icmp = 1,
    IcmpV6 = 58,
}

public enum ConnectionDirection
{
    Unknown = 0,
    Outbound = 1,
    Inbound = 2,
}

/// <summary>Вердикт, который система вынесла соединению.</summary>
public enum ObservedVerdict
{
    Unknown = 0,
    Allowed = 1,
    Dropped = 2,
}
