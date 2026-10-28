namespace NetZapret.Proxy;

/// <summary>
/// Как Xray понимает WebSocket и httpupgrade из ссылки — и чего sing-box сам не сделает.
/// </summary>
/// <remarks>
/// <para>
/// Выпуск Zapret KVN 0.8.49 (28.10, то же ядро sing-box-extended 2.7.3): «Trojan
/// и VMess через WebSocket снова передают трафик: исправлены ALPN h2, «?ed=» в пути
/// и httpupgrade». Сверено с нашим компилятором в тот же день — обе ошибки были
/// и у нас: ALPN и путь шли в конфиг как в ссылке.
/// </para>
/// <para>
/// ALPN. WebSocket и httpupgrade — это переход с HTTP/1.1 (Upgrade); по HTTP/2
/// его нет. Ссылки часто несут <c>alpn=h2,http/1.1</c>, сервер или CDN выбирает
/// h2 — и переход не случается. Xray у этих транспортов предлагает только
/// http/1.1, что бы ни стояло в ссылке.
/// </para>
/// <para>
/// <c>?ed=2048</c> в пути у Xray — не путь, а «ранние данные»: первые байты
/// уходят в заголовке <c>Sec-WebSocket-Protocol</c> запроса перехода, и сам
/// хвост на сервер не идёт. sing-box этого из пути не читает: у него
/// <c>max_early_data</c> и <c>early_data_header_name</c>. Без перевода путь
/// уходил с хвостом буквально, а ранних данных не было.
/// </para>
/// <para>
/// Вживую не проверено: серверов на WebSocket у владельца нет.
/// </para>
/// </remarks>
public static class WebSocketPath
{
    /// <summary>Заголовок ранних данных Xray.</summary>
    public const string EarlyDataHeader = "Sec-WebSocket-Protocol";

    /// <summary>Путь без <c>ed</c> и размер ранних данных; <c>ed</c> нет — путь как есть и 0.</summary>
    public static (string? Path, int EarlyData) Split(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return (path, 0);

        int question = path.IndexOf('?');
        if (question < 0)
            return (path, 0);

        int earlyData = 0;
        var kept = new List<string>();

        foreach (var pair in path[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];

            if (key.Equals("ed", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(eq < 0 ? string.Empty : pair[(eq + 1)..], out var size) && size > 0)
            {
                earlyData = size;
                continue;
            }

            kept.Add(pair);
        }

        if (earlyData == 0)
            return (path, 0);

        var bare = path[..question];
        return (kept.Count == 0 ? bare : bare + "?" + string.Join('&', kept), earlyData);
    }

    /// <summary>ALPN для транспорта: у WebSocket и httpupgrade — без h2 и h3.</summary>
    public static IReadOnlyList<string> Alpn(string? transport, IReadOnlyList<string> alpn)
    {
        if (transport is null
            || !(transport.Equals("ws", StringComparison.OrdinalIgnoreCase) || transport.Equals("httpupgrade", StringComparison.OrdinalIgnoreCase)))
        {
            return alpn;
        }

        var kept = alpn.Where(a => !a.Equals("h2", StringComparison.OrdinalIgnoreCase) && !a.Equals("h3", StringComparison.OrdinalIgnoreCase)).ToList();
        return kept.Count > 0 ? kept : ["http/1.1"];
    }
}
