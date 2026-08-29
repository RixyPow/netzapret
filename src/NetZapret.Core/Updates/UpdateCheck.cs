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
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

            if (tag is null)
                return null;

            string? url = null;
            long size = 0;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;

                    if (name is null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    break;
                }
            }

            if (url is null)
                return null;

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
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Новее ли найденный выпуск, чем идущий сейчас.
    /// </summary>
    /// <remarks>
    /// Сравнение по номерам, а не по строкам: «0.10.0» больше «0.9.0», хотя
    /// как строка меньше. Неразобранная версия считается не новее — ошибиться
    /// в эту сторону безопаснее, чем предлагать откат, приняв мусор за номер.
    /// </remarks>
    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Normalise(candidate), out var newer))
            return false;

        if (!Version.TryParse(Normalise(current), out var running))
            return false;

        return newer > running;
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
            var version = typeof(UpdateCheck).Assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
