namespace NetZapret.Core;

/// <summary>Как движок туннеля спрашивает имена (<see cref="AppSettings.DnsVia"/>).</summary>
public enum DnsRoute
{
    /// <summary>Сам решает: через туннель, пока он жив, иначе напрямую.</summary>
    Auto,

    /// <summary>Напрямую, своим соединением по DoH.</summary>
    Direct,

    /// <summary>Внутри туннеля, всегда.</summary>
    Tunnel,
}

/// <summary>Слова для выбора пути DNS — одни в окне, отчёте и nz.</summary>
public static class DnsRoutes
{
    public static string Word(DnsRoute route) => route switch
    {
        DnsRoute.Direct => "напрямую",
        DnsRoute.Tunnel => "через туннель",
        _ => "авто",
    };

    /// <summary>Разбор слова из nz: «авто», «напрямую», «туннель» и английские.</summary>
    public static DnsRoute? Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "авто" or "auto" => DnsRoute.Auto,
        "напрямую" or "direct" => DnsRoute.Direct,
        "туннель" or "через туннель" or "tunnel" => DnsRoute.Tunnel,
        _ => null,
    };
}
