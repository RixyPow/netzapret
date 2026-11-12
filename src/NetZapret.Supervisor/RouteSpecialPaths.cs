using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Supervisor;

/// <summary>
/// Какие особые пути (<see cref="RouteSpecial"/>) есть у маршрута — для окна пути и автоподбора.
/// </summary>
public static class RouteSpecialPaths
{
    /// <summary>Список приложения Telegram — его путь «TG Proxy».</summary>
    public const string TelegramApp = "config/lists/ipset-telegram.txt";

    /// <summary>Часть — приложение Telegram.</summary>
    public static bool IsTelegramApp(string? list) =>
        list is not null && string.Equals(list.Replace('\\', '/').Trim(), TelegramApp, StringComparison.OrdinalIgnoreCase);

    /// <summary>Есть ли прокси Telegram в поставке.</summary>
    public static bool TgProxyShipped(string? baseDirectory = null) => File.Exists(TgWsProxy.Executable(baseDirectory));

    private static readonly object Gate = new();
    private static (string Key, IReadOnlyDictionary<string, PinSolution> Homes)? _cache;

    /// <summary>
    /// Пин сервиса, чья часть — этот список; <c>null</c> — у части его нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Решение относится к части, которая покрывает больше всего его имён
    /// (сверка 13.11: Instagram — instagram.txt 7 из 8, Discord — discord.txt
    /// 5 из 6, голос — discord-media.txt 348 из 348). Только одной: пин Discord,
    /// поставленный из «Обновлений», прибил бы и имена API. Ничья — у меньшего
    /// списка (картинки Roblox: roblox-cdn.txt, а не roblox.txt).
    /// </para>
    /// <para>
    /// Большие перечни (реестр блокировок, <see cref="ServicePart.Bulk"/>) и части
    /// по адресам не в счёт. У ntc.party, Render и Supercell части в «Маршрутах»
    /// нет — они остаются в «Пинах напрямую» настроек маршрутов.
    /// </para>
    /// </remarks>
    public static PinSolution? SolutionFor(string list, string? zapretRoot = null, string? solutionsPath = null)
    {
        var homes = Homes(zapretRoot, solutionsPath);
        return homes.TryGetValue(Normalize(list), out var solution) ? solution : null;
    }

    /// <summary>Решения без своей части — им место в настройках маршрутов.</summary>
    public static IReadOnlyList<PinSolution> Homeless(string? zapretRoot = null, string? solutionsPath = null)
    {
        var homes = Homes(zapretRoot, solutionsPath).Values.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PinSolutions.Load(solutionsPath).Where(s => !homes.Contains(s.Id)).ToList();
    }

    private static IReadOnlyDictionary<string, PinSolution> Homes(string? zapretRoot, string? solutionsPath)
    {
        var path = solutionsPath ?? PinSolutions.DefaultPath;
        var key = $"{Path.GetFullPath(path)}|{(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0)}|{zapretRoot}";

        lock (Gate)
        {
            if (_cache is { } cached && cached.Key == key)
                return cached.Homes;
        }

        var parts = ServiceCatalog.All
            .SelectMany(s => s.Parts)
            .Where(p => !p.ByAddress && !p.Bulk)
            .Select(p => (List: Normalize(p.List), Zones: Zones(p.List, zapretRoot)))
            .Where(p => p.Zones.Count > 0)
            .ToList();

        var homes = new Dictionary<string, PinSolution>(StringComparer.OrdinalIgnoreCase);

        foreach (var solution in PinSolutions.Load(solutionsPath))
        {
            var best = parts
                .Select(p => (p.List, p.Zones.Count, Hits: solution.Names.Count(n => Covers(p.Zones, n))))
                .Where(p => p.Hits > 0)
                .OrderByDescending(p => p.Hits)
                .ThenBy(p => p.Count)
                .FirstOrDefault();

            // Первое решение на часть — его; второе на ту же часть (GitHub и его
            // загрузки) окно не покажет: путь у части один.
            if (best.List is not null && !homes.ContainsKey(best.List))
                homes[best.List] = solution;
        }

        lock (Gate)
            _cache = (key, homes);

        return homes;
    }

    private static IReadOnlyList<string> Zones(string list, string? zapretRoot)
    {
        try
        {
            return HostListReader.Read(list, zapretRoot, out _)
                .Select(d => d.TrimStart('*', '.').ToLowerInvariant())
                .Where(d => d.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool Covers(IReadOnlyList<string> zones, string name) =>
        zones.Any(zone => string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string list) => list.Replace('\\', '/').Trim();
}
