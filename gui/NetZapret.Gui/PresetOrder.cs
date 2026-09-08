using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NetZapret.Gui;

/// <summary>
/// Порядок пресетов, заданный руками.
/// </summary>
/// <remarks>
/// <para>
/// Отдельный файл с одними именами, а не переименование самих пресетов
/// номерами. Имя пресета лежит в настройках как выбранное, и приписав
/// к файлу «01 », мы бы обнулили этот выбор — обход перестал бы работать
/// от перетаскивания строки в списке.
/// </para>
/// <para>
/// Консоль про этот файл не знает и показывает пресеты по алфавиту. Это
/// сознательно: порядок — дело показа, и переучивать ради него меню значило
/// бы править работающее.
/// </para>
/// </remarks>
public static class PresetOrder
{
    public static string DefaultPath => Path.Combine("config", "preset-order.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<string> Load(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;

            return File.Exists(target)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(target), Options) ?? []
                : [];
        }
        catch (Exception)
        {
            // Испорченный файл означает всего лишь порядок по умолчанию.
            return [];
        }
    }

    public static void Save(IEnumerable<string> names, string? path = null)
    {
        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(target, JsonSerializer.Serialize(names, Options), new UTF8Encoding(false));
    }

    /// <summary>
    /// Раскладывает по сохранённому порядку.
    /// </summary>
    /// <remarks>
    /// Незнакомые уходят в конец, сохраняя между собой прежний порядок:
    /// пресет, положенный в папку после того, как порядок был задан, должен
    /// появиться, а не пропасть из списка.
    /// </remarks>
    public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, Func<T, string> name)
    {
        var order = Load();

        if (order.Count == 0)
            return items;

        var place = order
            .Select((value, index) => (value, index))
            .ToDictionary(x => x.value, x => x.index, StringComparer.OrdinalIgnoreCase);

        return items
            .OrderBy(item => place.TryGetValue(name(item), out int at) ? at : int.MaxValue)
            .ToList();
    }
}
