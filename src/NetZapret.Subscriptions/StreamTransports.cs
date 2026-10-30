namespace NetZapret.Subscriptions;

/// <summary>
/// Транспорты VLESS, VMess и Trojan: имена Xray в именах движка и что движок не повезёт.
/// </summary>
/// <remarks>
/// <para>
/// До 30.10 незнакомый транспорт молча собирался обычным TCP: сборщик конфига
/// не находил для него секции и не писал никакой. Сервер «🇵🇱 Польша — XDNS»
/// (mKCP поверх DNS на порту 53) так стучался в TCP-порт 53 и получал отказ —
/// в окне это выглядело мёртвым сервером, а не непонятым. Теперь такой сервер
/// пропускается и причина видна в отчёте (<see cref="ProxyServer.Unusable"/>).
/// </para>
/// <para>
/// Синонимы — по Xray: <c>raw</c> — новое имя <c>tcp</c>, <c>splithttp</c> —
/// старое имя <c>xhttp</c>, <c>h2</c> — то же, что <c>http</c> (HTTP/2),
/// <c>kcp</c> у Xray — <c>mkcp</c> у сборки extended.
/// </para>
/// </remarks>
public static class StreamTransports
{
    /// <summary>Что умеет сборщик конфига (<c>SingBoxConfigCompiler.BuildTransport</c>).</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "tcp", "ws", "grpc", "http", "httpupgrade", "xhttp", "mkcp",
    };

    /// <summary>Имя транспорта из ссылки или конфига — в имя движка.</summary>
    public static string Normalize(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "tcp" : name.Trim().ToLowerInvariant();

        return value switch
        {
            "raw" => "tcp",
            "splithttp" => "xhttp",
            "h2" => "http",
            "kcp" => "mkcp",
            _ => value,
        };
    }

    /// <summary>Почему транспорт не повезти; <c>null</c> — повезёт.</summary>
    /// <param name="transport">Имя после <see cref="Normalize"/>.</param>
    /// <param name="tcpHeader">
    /// Маскировка TCP у Xray (<c>headerType</c> ссылки, <c>header.type</c> в rawSettings).
    /// <c>http</c> — подделка под HTTP/1.1, у sing-box её нет: без неё сервер
    /// не поймёт первый же пакет.
    /// </param>
    public static string? Problem(string transport, string? tcpHeader = null)
    {
        if (!Known.Contains(transport))
            return $"транспорт «{transport}» движок не знает";

        if (transport == "tcp"
            && !string.IsNullOrWhiteSpace(tcpHeader)
            && !tcpHeader.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return $"маскировку TCP «{tcpHeader.Trim()}» движок не знает";
        }

        return null;
    }
}
