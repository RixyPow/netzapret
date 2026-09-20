namespace NetZapret.Gui.Views;

/// <summary>Чем раскладывать список сервисов.</summary>
internal enum RouteOrderBy
{
    /// <summary>Порядок каталога: сверху то, что ломается чаще.</summary>
    Catalog,

    /// <summary>По названию.</summary>
    Name,

    /// <summary>По тому, куда сервис идёт.</summary>
    Method,
}

/// <summary>
/// Раскладка списка маршрутов.
/// </summary>
/// <remarks>
/// <para>
/// Порядок каталога не случаен: сверху то, что ломается чаще, — Discord,
/// YouTube, Telegram. Он хорош, пока помнишь его наизусть, и плох, когда
/// ищешь Zoom среди семидесяти строк. Алфавит отвечает на второй случай.
/// </para>
/// <para>
/// По методу — просьба владельца 21.09, и отвечает она на третий вопрос:
/// «что у меня вообще идёт через VPN». Перебирать ради него семьдесят
/// строк и читать у каждой подпись — ровно та работа, которую machine
/// делает лучше.
/// </para>
/// <para>
/// Отдельно от вкладки затем, что это её чистая часть: раскладку можно
/// проверить без окна, а обработчик выбора — нет.
/// </para>
/// </remarks>
internal static class RouteOrder
{
    /// <summary>
    /// Каким числом сортировать сервис по методу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок тот же, что в списке выбора у каждой строки: напрямую,
    /// десинк, через VPN. Свой порядок здесь был бы третьим на одном
    /// экране и заставлял бы каждый раз вспоминать, какой сейчас.
    /// </para>
    /// <para>
    /// У сервиса из нескольких частей берётся преобладающий метод:
    /// у Discord их пять, и одна выбивающаяся не должна уводить его
    /// из своей группы. При равенстве побеждает больший — то есть
    /// более вмешивающийся: смешанный сервис уместнее видеть среди
    /// тех, где что-то настроено, чем среди нетронутых.
    /// </para>
    /// </remarks>
    public static int MethodOf(ServiceRow row)
    {
        if (row.Parts.Count == 0)
            return 0;

        return row.Parts
            .GroupBy(p => p.Choice)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First()
            .Key;
    }

    public static IReadOnlyList<ServiceRow> Apply(
        IEnumerable<ServiceRow> rows,
        RouteOrderBy by) => by switch
    {
        RouteOrderBy.Name =>
            rows.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),

        // Внутри группы — по алфавиту. Иначе внутри «через VPN» остаётся
        // порядок каталога, и найти там нужное не легче, чем во всём списке.
        RouteOrderBy.Method =>
            rows.OrderBy(MethodOf)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

        _ => rows.ToList(),
    };

    /// <summary>Как зовётся метод — теми же словами, что в списке выбора.</summary>
    public static string Name(int method) => method switch
    {
        0 => "напрямую",
        1 => "десинк",
        _ => "через VPN",
    };
}
