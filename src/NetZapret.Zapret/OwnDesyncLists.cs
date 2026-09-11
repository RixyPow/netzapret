using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetZapret.Zapret;

/// <summary>
/// Передаёт свои профили десинка от сборки конфига к запуску движка.
/// </summary>
/// <remarks>
/// <para>
/// Разнесены они по разным процессам: правила читает окно, когда собирает
/// конфиг, а командную строку winws2 собирает супервизор — отдельный процесс,
/// поднятый с правами администратора. Передать в него список аргументов
/// напрямую нельзя, а разбирать правила заново там значило бы завести второе
/// место, где решается, чем чинить имя. Разъехавшись, эти два места дали бы
/// настройку, которая показана одна, а применена другая.
/// </para>
/// <para>
/// Поэтому посередине файл: одна сторона его пишет, другая читает, и оба
/// видят одно и то же.
/// </para>
/// </remarks>
public static class OwnDesyncLists
{
    public static string DefaultPath => Path.Combine("runtime", "desync-own.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(IReadOnlyList<OwnDesyncProfile> profiles, string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Пустой список тоже записывается: иначе снятый рецепт продолжал
            // бы применяться из вчерашнего файла, и снять его было бы нечем.
            File.WriteAllText(
                target,
                JsonSerializer.Serialize(profiles, Options),
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Не записалось — движок поднимется на одном пресете. Это хуже
            // задуманного, но не хуже, чем было до выбора рецептов.
        }
    }

    public static IReadOnlyList<OwnDesyncProfile> Read(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            if (!File.Exists(target))
                return [];

            var profiles = JsonSerializer.Deserialize<List<OwnDesyncProfile>>(
                File.ReadAllText(target), Options) ?? [];

            // Список мог не пережить смену пресета или чистку каталога.
            // Ссылка на несуществующий файл заставила бы winws2 ругаться
            // при каждом запуске, ничего при этом не делая.
            return profiles
                .Where(p => p.Steps.Count > 0 && File.Exists(p.HostListPath))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
