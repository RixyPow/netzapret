using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetZapret.Core.Rules;

namespace NetZapret.Core;

/// <summary>
/// Выбор пользователя: что запускать и как.
/// </summary>
/// <remarks>
/// Появились ради меню. До него режим жил в файле правил, а пресет и сервер —
/// в аргументах командной строки, и запомнить выбор между запусками было негде.
/// Здесь же лежит то, что человек выбирает руками; сами правила остаются
/// в своём YAML и этим файлом не переписываются.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Ссылка на подписку.</summary>
    public string? SubscriptionUrl { get; init; }

    /// <summary>
    /// Базовый файл правил. Поверх него накладывается пользовательский слой
    /// из <see cref="Rules.UserRulesFile.DefaultPath"/>.
    /// </summary>
    /// <remarks>
    /// По умолчанию — рабочий <c>rules.yaml</c>, а не <c>rules.example.yaml</c>.
    /// Ссылка на пример однажды уже стоила поломки: конфиг пересобрался
    /// из примера, и Telegram потерял маршрут через VPN.
    /// </remarks>
    public string RulesPath { get; init; } = Path.Combine("config", "rules.yaml");

    /// <summary>
    /// Режим работы. Перекрывает значение из файла правил.
    /// </summary>
    /// <remarks>
    /// Перекрытие, а не правка YAML: файл правил пишет человек, там комментарии
    /// и порядок строк, и лезть в него из меню значит рано или поздно его испортить.
    /// </remarks>
    public OperatingMode Mode { get; init; } = OperatingMode.Selective;

    /// <summary>Название пресета Zapret; <c>null</c> — не запускать десинк.</summary>
    public string? PresetName { get; init; }

    /// <summary>Тег сервера либо <c>null</c> для автоподбора по задержке.</summary>
    public string? PreferredServer { get; init; }

    public string ProxyConfigPath { get; init; } = Path.Combine("runtime", "singbox.json");

    /// <summary>Заводить в туннель только трафик прокси.</summary>
    public bool ProxyOnly { get; init; } = true;

    /// <summary>Проверять не только порт, но и реальный проход трафика.</summary>
    public bool VerifyTraffic { get; init; }

    public static string DefaultPath => Path.Combine("config", "netzapret.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        if (!File.Exists(target))
            return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(target), Options)
                ?? new AppSettings();
        }
        catch (Exception)
        {
            // Испорченный файл настроек не повод не запуститься: берём значения
            // по умолчанию, а сохранение перезапишет его корректным.
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(target, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
    }

    public string DescribeMode() => Mode switch
    {
        OperatingMode.Off => "выключено",
        OperatingMode.DesyncOnly => "только десинк",
        OperatingMode.ProxyAll => "всё через VPN, кроме РФ",
        OperatingMode.ProxyStrict => "всё через VPN без исключений",
        _ => "выборочно",
    };

    /// <summary>Нужен ли в этом режиме туннель.</summary>
    /// <remarks>
    /// Поднимать sing-box там, где он никуда не ведёт, значит без причины
    /// держать TUN и путать поиск неисправностей: адаптер есть, маршруты
    /// стоят, а трафик через них не идёт.
    /// </remarks>
    public bool NeedsProxy => Mode is OperatingMode.Selective
        or OperatingMode.ProxyAll
        or OperatingMode.ProxyStrict;

    /// <summary>Нужен ли в этом режиме десинк.</summary>
    public bool NeedsDesync => Mode is not OperatingMode.Off && PresetName is not null;

    public string DescribeServer() => PreferredServer ?? "авто (по задержке)";

    public string DescribePreset() => PresetName ?? "не запускать";
}
