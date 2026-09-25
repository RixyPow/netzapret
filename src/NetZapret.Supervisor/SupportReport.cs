using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetZapret.Core;

namespace NetZapret.Supervisor;

/// <summary>Что получилось: где лежит архив и что в него вошло.</summary>
public sealed record SupportReportResult(string Path, IReadOnlyList<string> Files);

/// <summary>
/// Отчёт для разбора: журналы и настройки одним архивом, без ссылок и ключей.
/// </summary>
/// <remarks>
/// <para>
/// 24.09 жалоба «подписка не прочиталась: Этот хост неизвестен» упёрлась
/// в то, что разбирать было нечем: пришлось писать человеку, что где
/// посмотреть, и ждать. Одним файлом он отвечает сразу — и тем, что нужно,
/// а не тем, что он догадался скопировать.
/// </para>
/// <para>
/// Ссылка на подписку равносильна паролю, и человек, отправляющий отчёт
/// в общий чат, её не заметит. Поэтому вычистка — не вежливость, а условие:
/// ссылки подписок убираются дословно, прочие ссылки теряют путь, ссылки
/// прокси и ключи — целиком (<see cref="Redact"/>). Файлы с ключами —
/// singbox.json, subscriptions.json, warp.json — не берутся вовсе.
/// </para>
/// <para>
/// Журналы — начало и конец, а не хвост: первая ошибка падает по своей
/// причине, остальные — по её следам (CLAUDE.md), и в хвосте она не видна.
/// </para>
/// </remarks>
public static class SupportReport
{
    /// <summary>Куда кладётся архив: рядом с отчётами проверки блокировок.</summary>
    public static string DefaultDirectory => "reports";

    /// <summary>Сколько строк журнала брать с начала и с конца.</summary>
    private const int HeadLines = 400;
    private const int TailLines = 600;

    /// <summary>Собирает архив.</summary>
    /// <param name="version">Версия с номером сборки — как в заголовке окна.</param>
    /// <param name="secrets">
    /// Что ещё спрятать дословно. Ссылки подписок из настроек и книги
    /// подписок отчёт находит сам — забыть их передать нельзя.
    /// </param>
    /// <param name="root">Корень установки; <c>null</c> — рабочий каталог (окно уходит в корень при запуске).</param>
    public static SupportReportResult Create(
        string version,
        IEnumerable<string?>? secrets = null,
        string? directory = null,
        string? root = null,
        DateTime? now = null)
    {
        var when = now ?? DateTime.Now;
        var settings = AppSettings.Load(At(root, AppSettings.DefaultPath));
        var state = SupervisorState.Load(At(root, SupervisorState.DefaultPath));

        var book = BookUrls(At(root, Path.Combine("config", "subscriptions.json")));

        var hidden = (secrets ?? [])
            .Append(settings.SubscriptionUrl)
            .Concat(book)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parts = new List<(string Name, string Text)>
        {
            // Имена файлов — латиницей. Русские в архиве у части распаковщиков
            // выходят кракозябрами — так показал unzip из Git 25.09. Отчёт уходит
            // к чужим людям с чужими распаковщиками.
            ("summary.txt", Summary(version, settings, state, book.Count, when)),

            // Ссылку из настроек убираем до сериализации, а не надеемся
            // на вычистку: поле ради того и названо.
            ("settings.json", JsonSerializer.Serialize(
                settings with { SubscriptionUrl = null },
                new JsonSerializerOptions { WriteIndented = true })),
        };

        AddLog(parts, "supervisor.log", At(root, Path.Combine("runtime", "supervisor.log")));
        AddLog(parts, "sing-box.log", At(root, Path.Combine("runtime", "sing-box.log")));
        AddLog(parts, "winws2.log", At(root, Path.Combine("runtime", "winws2.log")));
        AddFile(parts, "rules.user.yaml", At(root, Path.Combine("config", "rules.user.yaml")));
        AddFile(parts, "desync-exclude.txt", At(root, Path.Combine("runtime", "desync-exclude.txt")));

        var folder = directory ?? At(root, DefaultDirectory);
        Directory.CreateDirectory(folder);

        var path = Path.GetFullPath(Path.Combine(folder, $"netzapret-report-{when:yyyyMMdd-HHmmss}.zip"));

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in parts)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Redact(text, hidden));
            }
        }

        return new SupportReportResult(path, parts.Select(p => p.Name).ToList());
    }

    private static string Summary(
        string version,
        AppSettings settings,
        SupervisorState? state,
        int subscriptions,
        DateTime when)
    {
        var text = new StringBuilder();

        text.AppendLine($"NetZapret {version}");
        text.AppendLine($"Снят: {when:dd.MM.yyyy HH:mm:ss}");
        text.AppendLine($"Windows: {Environment.OSVersion.Version}");
        text.AppendLine();
        text.AppendLine($"Режим: {settings.Engines.Describe()}");
        text.AppendLine($"Игнорировать исключения: {YesNo(settings.Engines.IgnoreExclusions)}");
        text.AppendLine($"Пресет: {settings.PresetName ?? "не выбран"}");
        text.AppendLine($"DNS: {settings.DnsServer}, {(settings.DnsThroughTunnel ? "через туннель" : "напрямую")}");
        text.AppendLine($"WARP: {YesNo(settings.WarpEnabled)}");
        text.AppendLine($"Подписок: {subscriptions} (ссылки в отчёт не входят)");
        text.AppendLine($"Проверка прохода трафика: {YesNo(settings.VerifyTraffic)}");
        text.AppendLine();

        if (state is null)
        {
            text.AppendLine("Надзор: движки не поднимались");
        }
        else
        {
            text.AppendLine($"Надзор: {(state.IsSupervisorAlive() ? "жив" : "не отвечает")}");

            foreach (var service in state.Services)
            {
                text.Append($"  {service.Name}: {service.Health}");

                if (service.RestartCount > 0)
                    text.Append($", перезапусков {service.RestartCount}");

                if (service.StartedAt is { } started)
                    text.Append($", поднят {started.ToLocalTime():dd.MM HH:mm:ss}");

                text.AppendLine();

                if (!string.IsNullOrWhiteSpace(service.LastError))
                    text.AppendLine($"    последняя ошибка: {service.LastError}");
            }
        }

        return text.ToString();
    }

    private static string YesNo(bool value) => value ? "да" : "нет";

    /// <summary>
    /// Ссылки из книги подписок — все строки полей <c>Url</c>.
    /// </summary>
    /// <remarks>
    /// Книгу ведёт окно, и её класс живёт там же; nz его не видит. Поэтому
    /// разбирается сам файл, не завися от формата записей: любое поле Url
    /// на любой глубине — ссылка, которую надо спрятать.
    /// </remarks>
    private static List<string> BookUrls(string path)
    {
        var urls = new List<string>();

        if (Read(path) is not { } json)
            return urls;

        try
        {
            using var document = JsonDocument.Parse(json);
            Collect(document.RootElement);
        }
        catch (JsonException)
        {
            // Битая книга — прятать из неё нечего, но и падать незачем.
        }

        return urls;

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Equals("Url", StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.String
                            && property.Value.GetString() is { Length: > 0 } url)
                        {
                            urls.Add(url);
                        }
                        else
                        {
                            Collect(property.Value);
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Collect(item);

                    break;
            }
        }
    }

    /// <summary>Путь от корня установки; без корня — от рабочего каталога, как у окна.</summary>
    private static string At(string? root, string relative) =>
        string.IsNullOrEmpty(root) ? relative : Path.Combine(root, relative);

    private static void AddLog(List<(string, string)> parts, string name, string path)
    {
        if (Read(path) is { } text)
            parts.Add((name, Excerpt(text, HeadLines, TailLines)));
    }

    private static void AddFile(List<(string, string)> parts, string name, string path)
    {
        if (Read(path) is { } text)
            parts.Add((name, text));
    }

    /// <summary>Читает, не мешая писателю: журналы открыты движками на запись.</summary>
    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Начало и конец текста, с пометкой, сколько пропущено между.</summary>
    public static string Excerpt(string text, int head, int tail)
    {
        var lines = text.Split('\n');

        if (lines.Length <= head + tail)
            return text;

        return string.Join('\n', lines.Take(head))
            + $"\n\n… пропущено строк: {lines.Length - head - tail} …\n\n"
            + string.Join('\n', lines.Skip(lines.Length - tail));
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>Ссылки прокси целиком: в них ключ стоит до адреса.</summary>
    private static readonly Regex ProxyLink = new(
        @"\b(?:vless|vmess|trojan|ss|ssr|hysteria2?|hy2|tuic|wireguard|wg|socks5?|anytls|naive\+https)://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Прочие ссылки: имя узла оставляем — по нему и разбирают, — путь и запрос нет.</summary>
    private static readonly Regex WebLink = new(
        @"\b(https?://[^/\s""'<>]+)[/?][^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Uuid = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    /// <summary>Поля с ключами в JSON и YAML: значение прячется, имя поля остаётся.</summary>
    private static readonly Regex KeyField = new(
        @"(""?(?:password|passwd|private_key|privateKey|public_key|publicKey|pre_shared_key|short_id|shortId|uuid|token|secret|auth_str|obfs_password)""?\s*[:=]\s*)(""[^""]*""|[^\s,}\]]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Вычищает из текста то, чего в отчёте быть не должно.
    /// </summary>
    /// <remarks>
    /// Порядок важен. Ссылки подписок — первыми и дословно: правило для
    /// прочих ссылок оставило бы от них имя панели, а это лишнее. Путь профиля
    /// Windows заменяется, потому что в нём имя учётной записи человека.
    /// </remarks>
    public static string Redact(string text, IReadOnlyList<string> secrets)
    {
        var result = Ansi.Replace(text, string.Empty);

        foreach (var secret in secrets.OrderByDescending(s => s.Length))
            result = result.Replace(secret, "<ссылка подписки скрыта>", StringComparison.OrdinalIgnoreCase);

        result = ProxyLink.Replace(result, "<ссылка прокси скрыта>");
        result = WebLink.Replace(result, "$1/…");
        result = KeyField.Replace(result, "$1\"<скрыто>\"");
        result = Uuid.Replace(result, "<uuid>");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(profile))
        {
            result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            // Движок под Cygwin пишет тот же путь по-своему:
            // /cygdrive/c/Users/имя — и имя в нём то же.
            var cygwin = "/cygdrive/" + char.ToLowerInvariant(profile[0]) + profile[2..].Replace('\\', '/');
            result = result.Replace(cygwin, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
