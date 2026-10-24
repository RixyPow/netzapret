namespace NetZapret.Subscriptions;

/// <summary>
/// Смена портов у Hysteria2 (port hopping): списки портов из ссылок
/// и конфигов — в запись <c>server_ports</c> движка.
/// </summary>
/// <remarks>
/// <para>
/// Сервер со сменой портов принимает соединения на целом диапазоне, а клиент
/// раз в 30 секунд переходит на случайный порт из него новым потоком UDP.
/// По документации Hysteria, так обходят провайдеров, которые душат долгий
/// поток UDP к одному порту.
/// </para>
/// <para>
/// Записывают её трояко. Официальная ссылка — списком прямо в адресе:
/// <c>hysteria2://пароль@host:443,20000-50000/?…</c>. v2rayN — параметром
/// <c>mport=20000-50000</c> при обычном порте. Конфиг sing-box — полем
/// <c>server_ports: ["20000:50000"]</c>. Движок понимает только последнее,
/// и строго: одиночный порт без двоеточия («443») и диапазон через дефис
/// роняют весь конфиг — «bad port range» (24.10, 1.14.3-extended-2.7.3).
/// Отсюда одна запись на всё: <c>443:443,20000:50000</c>.
/// </para>
/// </remarks>
public static class PortHopping
{
    /// <summary>
    /// Приводит список портов к записи движка.
    /// </summary>
    /// <param name="spec">
    /// Порты через запятую; каждый — число или диапазон через дефис либо двоеточие.
    /// </param>
    /// <returns>
    /// <c>443:443,20000:50000</c> или <c>null</c>, если хоть один элемент
    /// не порт: недоразобранный список увёл бы клиент на чужие порты.
    /// </returns>
    public static string? Normalize(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        var ranges = new List<string>();

        foreach (var raw in spec.Split(','))
        {
            var item = raw.Trim();
            if (item.Length == 0)
                continue;

            int separator = item.IndexOfAny(['-', ':']);
            var (first, last) = separator < 0 ? (item, item) : (item[..separator], item[(separator + 1)..]);

            if (!TryPort(first, out var start) || !TryPort(last, out var end))
                return null;

            // Перевёрнутый диапазон движок разворачивает в пустой список
            // и молча подключается без смены (sing-quic, ParsePorts);
            // официальный клиент Hysteria такой переворачивает — так и здесь.
            if (start > end)
                (start, end) = (end, start);

            var range = $"{start}:{end}";
            if (!ranges.Contains(range))
                ranges.Add(range);
        }

        return ranges.Count > 0 ? string.Join(',', ranges) : null;
    }

    /// <summary>
    /// Вынимает список портов из адреса официальной ссылки:
    /// <c>пароль@host:443,20000-50000/?…</c> → <c>пароль@host:443/?…</c>.
    /// </summary>
    /// <param name="rest">Ссылка после <c>://</c>.</param>
    /// <returns>
    /// Ссылку с первым портом списка вместо всего списка — её дальше разбирает
    /// обычный разбор — и сам список в записи движка. Нет списка в адресе —
    /// ссылка как была и <c>null</c>.
    /// </returns>
    /// <exception cref="FormatException">Список в адресе не разбирается.</exception>
    public static (string Link, string? Ports) FromAddress(string rest)
    {
        // Адрес кончается там же, где его обрезает разбор ссылки:
        // на первом «#», «?» или «/».
        int end = rest.IndexOfAny(['#', '?', '/']);
        var address = end < 0 ? rest : rest[..end];

        // Порт — за последним «:», и только если тот стоит после пароля
        // и после скобки адреса IPv6.
        int colon = address.LastIndexOf(':');
        if (colon < 0 || colon < address.LastIndexOf('@') || colon < address.LastIndexOf(']'))
            return (rest, null);

        var spec = address[(colon + 1)..];
        if (!spec.Contains(',') && !spec.Contains('-'))
            return (rest, null);

        var ports = Normalize(spec) ?? throw new FormatException($"порты '{spec}' не разбираются");
        return (rest[..(colon + 1)] + First(ports) + rest[address.Length..], ports);
    }

    /// <summary>
    /// Порты смены из параметра <c>mport</c> (запись v2rayN).
    /// </summary>
    /// <remarks>
    /// Как у v2rayN, смена — только при списке или диапазоне: одиночный
    /// «mport=8443» при порте 443 увёл бы клиент с порта, который назван
    /// в адресе. Неразборчивый mport пропускается: сервер работает и без
    /// смены, на своём порте, — как работал до 24.10.
    /// </remarks>
    public static string? FromParameter(string? mport) =>
        mport is not null && mport.IndexOfAny([',', '-', ':']) >= 0 ? Normalize(mport) : null;

    /// <summary>Порты смены вместе, без повторов; <c>null</c> — смены нет.</summary>
    public static string? Join(params string?[] lists)
    {
        var all = lists.Where(l => l is not null).ToArray();
        return all.Length == 0 ? null : Normalize(string.Join(',', all));
    }

    /// <summary>Первый порт записи — он стоит в адресе сервера в окне.</summary>
    /// <param name="ports">Запись движка, как её возвращает <see cref="Normalize"/>.</param>
    public static ushort First(string ports) => ushort.Parse(ports.AsSpan(0, ports.IndexOf(':')));

    /// <summary>Диапазоны записи по одному — элементы <c>server_ports</c>.</summary>
    public static IEnumerable<string> Ranges(string ports) => ports.Split(',');

    private static bool TryPort(string text, out ushort port) =>
        ushort.TryParse(text.Trim(), out port) && port > 0;
}
