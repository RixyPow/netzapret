using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetZapret.Mobile;

/// <summary>Чем делать «десинк» без root.</summary>
public enum DesyncEngine
{
    /// <summary>ByeDPI отдельным процессом: sing-box отдаёт ему имена «десинка» по SOCKS5.</summary>
    ByeDpi,

    /// <summary>Без ByeDPI: фрагментация приветствия TLS самим sing-box на правиле.</summary>
    Fragment,
}

/// <summary>
/// Откуда ByeDPI берёт стратегию. Имена — те же, что у <c>ByeDpiStrategy</c>
/// в <c>NetZapret.Zapret</c>: хозяин переводит одно в другое по имени.
/// </summary>
public enum DesyncStrategy
{
    /// <summary>Перевод выбранного пресета (те же секции, что у winws2 на Windows).</summary>
    Preset,

    /// <summary>Встроенная стратегия <c>ByeDpiCommandLine.DefaultStrategy</c>.</summary>
    Default,

    /// <summary>Своя строка ключей ByeDPI.</summary>
    Custom,
}

/// <summary>
/// Настройки, которых у Windows нет: чем делать десинк на телефоне.
/// </summary>
/// <remarks>
/// <para>
/// Отдельным файлом <c>config/mobile.json</c>, а не полями <c>AppSettings</c>:
/// общий файл настроек читает и окно Windows, и чужое ему поле там было бы
/// мусором, который при сохранении окно не сохранит. Всё общее — режим,
/// пресет, DNS, сервер — по-прежнему в <c>config/netzapret.json</c>.
/// </para>
/// <para>
/// Читает их хозяин (AndroidEngineHost) при запуске движков и собирает
/// из них <c>ByeDpiRequest</c>; интерфейс только пишет.
/// </para>
/// </remarks>
public sealed record MobileSettings
{
    public DesyncEngine Desync { get; init; } = DesyncEngine.ByeDpi;

    public DesyncStrategy Strategy { get; init; } = DesyncStrategy.Preset;

    /// <summary>Своя строка ключей ByeDPI при <see cref="DesyncStrategy.Custom"/>.</summary>
    public string? CustomArguments { get; init; }

    /// <summary>
    /// Брать nfqws2 с root, когда root есть. Выключено по умолчанию: главное
    /// у программы обязано работать без root (требование владельца), и root
    /// включается только руками.
    /// </summary>
    public bool UseRoot { get; init; }

    public static string DefaultPath => Path.Combine("config", "mobile.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Читает файл; нет его или испорчен — значения по умолчанию.</summary>
    /// <remarks>
    /// Испорченный файл не роняет запуск: десинк по пресету — то же, что
    /// человек получил бы, не трогая настроек вовсе.
    /// </remarks>
    public static MobileSettings Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            return File.Exists(target)
                ? JsonSerializer.Deserialize<MobileSettings>(File.ReadAllText(target), Options) ?? new()
                : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Через временный файл: телефон гасит процесс без предупреждения,
        // и полузаписанный файл означал бы сброс настроек при следующем чтении.
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(temp, target, overwrite: true);
    }

    public string DescribeDesync(bool root) => (Desync, root && UseRoot) switch
    {
        (_, true) => "nfqws2 (root) — пресет",
        (DesyncEngine.Fragment, _) => "фрагментация sing-box",
        _ => Strategy switch
        {
            DesyncStrategy.Default => "ByeDPI — стратегия по умолчанию",
            DesyncStrategy.Custom => "ByeDPI — своя строка",
            _ => "ByeDPI — по пресету",
        },
    };
}
