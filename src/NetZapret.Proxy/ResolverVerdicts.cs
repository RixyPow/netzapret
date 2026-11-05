using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetZapret.Proxy;

/// <summary>Что обзор резолверов сказал про резолвер у этого человека.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResolverVerdictKind>))]
public enum ResolverVerdictKind
{
    /// <summary>DoH к нему не проходит: туннель через него имён не узнает.</summary>
    Blocked,

    /// <summary>Открытый DNS к нему отдаёт подменённые ответы на закрытые имена.</summary>
    UdpSpoofed,

    /// <summary>DoH проходит, открытый DNS отвечает без подмены.</summary>
    Untouched,
}

/// <param name="Kind">Пометка.</param>
/// <param name="Detail">
/// Подробность для подсказки: у <see cref="ResolverVerdictKind.Blocked"/> — почему
/// DoH не прошёл («TLS DROP», «TIMEOUT»), у <see cref="ResolverVerdictKind.UdpSpoofed"/> —
/// сколько имён подменено из скольких; пусто — сказать нечего.
/// </param>
/// <param name="At">Когда проверено.</param>
public sealed record ResolverVerdict(ResolverVerdictKind Kind, string Detail, DateTime At);

/// <summary>
/// Пометки резолверов по обзору — и память о них между запусками окна.
/// </summary>
/// <remarks>
/// <para>
/// До 05.11 пометки жили, пока открыто окно: последний обзор держался в памяти
/// вкладки DNS, и после перезапуска «блокируется» у Comss и «UDP подменяют»
/// у Google и Cloudflare пропадали до следующего «Проверить» — владелец
/// попросил их запоминать. Задержка в память не идёт: с прошлого запуска она
/// уже ни о чём не говорит, а блокировка и подмена — свойство провайдера
/// человека, и меняются они не за минуты. Поэтому у пометки — время проверки:
/// окно говорит, когда это было.
/// </para>
/// <para>
/// Сеть, на которой мерилось, не запоминается: ноутбук в другой сети увидит
/// пометки своей домашней, пока не нажмёт «Проверить».
/// </para>
/// </remarks>
public static class ResolverVerdicts
{
    public static string DefaultPath => Path.Combine("runtime", "resolver-verdicts.json");

    /// <summary>Ключ резолвера — тот же, каким вкладка DNS ищет его строку.</summary>
    public static string Key(DnsProvider provider) =>
        provider.TlsAddress ?? (provider.Udp.Count > 0 ? provider.Udp[0] : provider.Name);

    /// <summary>
    /// Пометка по строке обзора; <c>null</c> — сказать нечего: не мерили или
    /// замер неполон (открытый DNS у резолвера есть, но не ответил).
    /// </summary>
    /// <remarks>
    /// «Не тронут» — только когда проверено всё, что у резолвера есть: DoH прошёл,
    /// а открытый DNS, если он есть, ответил и не подменил ни одного
    /// проверочного имени. Молчащий UDP — ни то ни другое: молчание бывает
    /// и блоком, и потерянным пакетом, и что из двух, обзор не различает.
    /// </remarks>
    public static ResolverVerdict? Judge(DnsSurveyRow row, DateTime at)
    {
        if (row.DohMs is null && row.DohFailure.Length > 0)
            return new ResolverVerdict(ResolverVerdictKind.Blocked, row.DohFailure, at);

        // Только настоящая подмена. «Наружу ходит из чужой сети» (Intercepted) —
        // признак, а не вердикт (DnsSurvey): обзор 05.11 у владельца поставил его
        // Quad9, AdGuard, CleanBrowsing, DNS.SB, Control D и XBOX при подмене
        // 0 из 5, а Quad9 по замерам 20.10 и 29.10 честен и по UDP. До 05.11
        // окно по одному признаку писало им «UDP подменяют».
        if (row.Spoofed > 0)
            return new ResolverVerdict(ResolverVerdictKind.UdpSpoofed, $"{row.Spoofed}/{row.SpoofChecked}", at);

        bool doh = row.DohMs is not null;
        bool udp = row.Provider.Udp.Count == 0 || row.UdpAnswered > 0 && row.SpoofChecked > 0;

        return doh && udp ? new ResolverVerdict(ResolverVerdictKind.Untouched, string.Empty, at) : null;
    }

    /// <summary>Запомненные пометки по ключу резолвера (<see cref="Key"/>); файла нет или он битый — пусто.</summary>
    public static IReadOnlyDictionary<string, ResolverVerdict> Load(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;

            if (!File.Exists(target))
                return new Dictionary<string, ResolverVerdict>();

            return JsonSerializer.Deserialize<Dictionary<string, ResolverVerdict>>(File.ReadAllText(target))
                ?? new Dictionary<string, ResolverVerdict>();
        }
        catch (Exception)
        {
            // Пометка — подсказка: без памяти вкладка просто покажет их после «Проверить».
            return new Dictionary<string, ResolverVerdict>();
        }
    }

    /// <summary>
    /// Запоминает пометки обзора: проверенные резолверы получают новую (или
    /// теряют старую, если сказать нечего), непроверенные — остаются как были.
    /// </summary>
    /// <remarks>
    /// Обзор, прерванный уходом с вкладки, проверил не всех — чужие пометки он
    /// не стирает.
    /// </remarks>
    public static IReadOnlyDictionary<string, ResolverVerdict> Remember(IEnumerable<DnsSurveyRow> rows, DateTime at, string? path = null)
    {
        var target = path ?? DefaultPath;
        var known = new Dictionary<string, ResolverVerdict>(Load(target));

        foreach (var row in rows)
        {
            var key = Key(row.Provider);

            if (Judge(row, at) is { } verdict)
                known[key] = verdict;
            else
                known.Remove(key);
        }

        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Через временный файл: прерванная запись не оставит битого.
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(known), new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception)
        {
            // Не записалось — в этом запуске пометки всё равно видны.
        }

        return known;
    }
}
