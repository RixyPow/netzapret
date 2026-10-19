using System.Text.Json;

namespace NetZapret.Core.Updates;

/// <summary>Что известно про доступный выпуск.</summary>
public sealed record ReleaseInfo
{
    public required string Version { get; init; }

    public required string Tag { get; init; }

    /// <summary>Ссылка на архив.</summary>
    public required string ArchiveUrl { get; init; }

    public long ArchiveSize { get; init; }

    /// <summary>Описание изменений — то, ради чего человек и решает, обновляться ли.</summary>
    public string? Notes { get; init; }

    public DateTimeOffset? Published { get; init; }
}

/// <summary>
/// Узнаёт, вышла ли версия новее нашей.
/// </summary>
/// <remarks>
/// <para>
/// Толчок был конкретный: правки, чинившие Steam, Roblox и распознавание
/// отказа по стране, дошли не до всех, кто на них жаловался. Обновление
/// целиком ручное, о выпуске человек узнаёт, только если сам зайдёт
/// на страницу релизов, — а не зайдёт, останется на версии без починок.
/// </para>
/// <para>
/// Только проверка и только по спросу. Установка — отдельный шаг с отдельным
/// согласием: подменять работающий обход молча нельзя, человек мог остаться
/// на старой версии намеренно.
/// </para>
/// </remarks>
public static class UpdateCheck
{
    public const string Repository = "RixyPow/netzapret";

    /// <summary>
    /// Спрашивает GitHub про последний выпуск.
    /// </summary>
    /// <returns><c>null</c>, если узнать не удалось.</returns>
    /// <remarks>
    /// Отказ здесь не ошибка программы: сеть может быть закрыта, GitHub
    /// недоступен, лимит запросов исчерпан. Молчание в таком случае честнее
    /// сообщения о поломке — обновление не состоялось, только и всего.
    /// </remarks>
    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // GitHub отклоняет запросы без User-Agent.
            http.DefaultRequestHeaders.Add("User-Agent", "NetZapret");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Repository}/releases/latest",
                cancellationToken);

            using var document = JsonDocument.Parse(json);

            return Read(document.RootElement) is { ArchiveUrl.Length: > 0 } release ? release : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Последние выпуски с их примечаниями — для «Что нового» за все пропущенные версии.
    /// </summary>
    /// <returns>Пусто, если узнать не удалось.</returns>
    /// <remarks>
    /// Владелец 06.10, по образцу Zapret GUI: окно обновления показывает
    /// чейнджлог каждой версии между установленной и новой, а не одной
    /// последней. Выпуски выходят по нескольку в день, и человек с 0.12.0
    /// иначе не узнал бы, что пришло в 0.12.1–0.13.0. Тридцати хватает
    /// с запасом: столько выходит недели за три.
    /// </remarks>
    public static async Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            http.DefaultRequestHeaders.Add("User-Agent", "NetZapret");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page=30",
                cancellationToken);

            return FromReleases(json);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Разбирает ответ GitHub со списком выпусков; черновики и предварительные — мимо.</summary>
    public static IReadOnlyList<ReleaseInfo> FromReleases(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ReleaseInfo>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            bool skipped = item.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True
                || item.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;

            if (!skipped && Read(item) is { } release)
                result.Add(release);
        }

        return result;
    }

    /// <summary>
    /// Выпуски новее установленного и не новее найденного — от нового к старому.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> Between(IEnumerable<ReleaseInfo> releases, string current, string latest) =>
        releases
            .Where(r => IsNewer(r.Version, current) && !IsNewer(r.Version, latest))
            .GroupBy(r => r.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.Version, Order)
            .ToList();

    /// <summary>Один выпуск из ответа GitHub; архива нет — пустая ссылка.</summary>
    private static ReleaseInfo? Read(JsonElement root)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

        if (tag is null)
            return null;

        string url = string.Empty;
        long size = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;

                if (name is null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                break;
            }
        }

        return new ReleaseInfo
        {
            Version = tag.TrimStart('v', 'V'),
            Tag = tag,
            ArchiveUrl = url,
            ArchiveSize = size,
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null,
            Published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var when)
                ? when
                : null,
        };
    }

    /// <summary>
    /// Новее ли найденный выпуск, чем идущий сейчас.
    /// </summary>
    /// <remarks>
    /// Сравнение по номерам, а не по строкам: «0.10.0» больше «0.9.0», хотя
    /// как строка меньше. Неразобранная версия считается не новее — ошибиться
    /// в эту сторону безопаснее, чем предлагать откат, приняв мусор за номер.
    /// </remarks>
    public static bool IsNewer(string candidate, string current) =>
        Parse(candidate) is { } newer && Parse(current) is { } running && Compare(newer, running) > 0;

    /// <summary>
    /// Порядок версий от старой к новой — для лент выпусков; неразобранные — первыми.
    /// </summary>
    public static IComparer<string> Order { get; } = Comparer<string>.Create((a, b) =>
        (Parse(a), Parse(b)) switch
        {
            ({ } x, { } y) => Compare(x, y),
            (null, null) => 0,
            (null, _) => -1,
            _ => 1,
        });

    /// <summary>
    /// Номер и суффикс предварительной версии: «v1.0.0-pre.1» → (1.0.0, «pre», 1).
    /// </summary>
    /// <remarks>
    /// С 19.10 версия бывает и с суффиксом (владелец: «поменяй версию на pre1
    /// 1.0.0» — записано по SemVer, 1.0.0-pre.1). Прежний разбор брал только
    /// числа и на «0-pre» отказывал: такой выпуск никогда не был бы новее,
    /// а установленный 1.0.0-pre.1 считался бы 1.0.0, и сам 1.0.0 ему не
    /// предложился бы. Метаданные после «+» (хеш коммита у InformationalVersion)
    /// в сравнение не идут.
    /// </remarks>
    private static (Version Number, string? Label, int Step)? Parse(string value)
    {
        var text = value.Trim().TrimStart('v', 'V').Split('+')[0];
        int dash = text.IndexOf('-');
        var number = dash < 0 ? text : text[..dash];

        if (!Version.TryParse(Normalise(number), out var parsed))
            return null;

        if (dash < 0)
            return (parsed, null, 0);

        // «pre.1», «pre1», «rc.2»: буквы — метка, хвостовые цифры — шаг.
        var suffix = text[(dash + 1)..];
        int digits = suffix.Length;

        while (digits > 0 && char.IsAsciiDigit(suffix[digits - 1]))
            digits--;

        var label = suffix[..digits].TrimEnd('.', '-').ToLowerInvariant();
        int step = digits < suffix.Length && int.TryParse(suffix[digits..], out var n) ? n : 0;

        return (parsed, label, step);
    }

    /// <summary>
    /// Числа первыми; при равных числах окончательная новее предварительной
    /// (1.0.0 &gt; 1.0.0-pre.1, как в SemVer), предварительные — по метке и шагу.
    /// </summary>
    private static int Compare((Version Number, string? Label, int Step) a, (Version Number, string? Label, int Step) b)
    {
        int byNumber = a.Number.CompareTo(b.Number);

        if (byNumber != 0)
            return byNumber;

        if (a.Label is null || b.Label is null)
            return (a.Label is null ? 1 : 0) - (b.Label is null ? 1 : 0);

        int byLabel = string.CompareOrdinal(a.Label, b.Label);

        return byLabel != 0 ? byLabel : a.Step.CompareTo(b.Step);
    }

    /// <summary>Приводит «v0.3.2» и «0.3.2.0» к виду, понятному разбору.</summary>
    private static string Normalise(string value)
    {
        var trimmed = value.Trim().TrimStart('v', 'V');
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // Version требует минимум двух чисел; одиночное «1» им не является.
        return parts.Length >= 2 ? string.Join('.', parts.Take(4)) : trimmed + ".0";
    }

    /// <summary>Версия, которая работает прямо сейчас.</summary>
    public static string Current
    {
        get
        {
            // InformationalVersion несёт суффикс («1.0.0-pre.1»), а номер сборки —
            // только числа: по нему 1.0.0-pre.1 выглядел бы как 1.0.0, и сам 1.0.0
            // не предложился бы обновлением.
            var informational = typeof(UpdateCheck).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion.Split('+')[0];

            if (!string.IsNullOrWhiteSpace(informational) && Parse(informational) is not null)
                return informational;

            var version = typeof(UpdateCheck).Assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
