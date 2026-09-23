using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetZapret.Core.Themes;

/// <summary>Тема, собранная в редакторе окна, — до записи в папку.</summary>
public sealed record ThemeDraft
{
    public required string Name { get; init; }

    public string? Author { get; init; }

    /// <summary>Все цвета явно: тема из редактора не зависит от основы.</summary>
    public required IReadOnlyDictionary<string, ThemeColor> Colors { get; init; }

    public string? DisplayFont { get; init; }

    /// <summary>Картинка фона — любой путь на диске; в тему кладётся копия.</summary>
    public string? BackgroundSource { get; init; }

    public BackgroundFit Fit { get; init; } = BackgroundFit.Cover;

    public double Dim { get; init; } = 0.5;

    public double Blur { get; init; } = 24;
}

/// <summary>
/// Записывает тему из редактора в <c>themes\</c> и возит темы файлом.
/// </summary>
/// <remarks>
/// <para>
/// Владелец, 24.09: «добавь возможность прям в программе создавать свои
/// темы и сохранять в файл». Тема из редактора — такая же папка
/// с theme.json, как тема художника, и читается тем же ThemeLoader с той же
/// проверкой: второго пути для «своих» тем быть не должно.
/// </para>
/// <para>
/// Встроенные темы не перезаписываются: их пришлось бы чинить после
/// каждого обновления, а поставленные с программой файлы обновление
/// вернуло бы молча. Правка встроенной сохраняется новой темой.
/// </para>
/// </remarks>
public static class ThemeWriter
{
    /// <summary>
    /// Записывает тему; <paramref name="id"/> — папка правимой темы, <c>null</c> — новая.
    /// </summary>
    /// <returns>Имя папки, в которую записано.</returns>
    public static string Save(ThemeDraft draft, string? id = null, string? root = null)
    {
        var themes = root ?? ThemeLoader.DefaultRoot;
        Directory.CreateDirectory(themes);

        var target = id is not null && !ThemeLoader.Shipped.Contains(id)
            ? id
            : Unique(Slug(draft.Name), themes);

        var folder = Path.Combine(themes, target);
        Directory.CreateDirectory(folder);

        var json = new JsonObject
        {
            ["name"] = draft.Name.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(draft.Author))
            json["author"] = draft.Author.Trim();

        var colors = new JsonObject();

        foreach (var slot in ThemeSlots.All)
        {
            if (draft.Colors.TryGetValue(slot, out var color))
                colors[slot] = color.ToString();
        }

        json["colors"] = colors;

        if (!string.IsNullOrWhiteSpace(draft.DisplayFont))
            json["fonts"] = new JsonObject { ["display"] = draft.DisplayFont.Trim() };

        if (draft.BackgroundSource is { } source)
        {
            var name = "background" + Path.GetExtension(source).ToLowerInvariant();
            var copy = Path.Combine(folder, name);

            // Картинка могла уже лежать в этой папке — тогда копировать
            // её саму в себя незачем, а File.Copy на тот же путь упал бы.
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
            {
                // Прежняя картинка другого формата осталась бы лежать мёртвым грузом.
                foreach (var old in Directory.GetFiles(folder, "background.*"))
                    File.Delete(old);

                File.Copy(source, copy, overwrite: true);
            }

            json["background"] = new JsonObject
            {
                ["image"] = name,
                ["fit"] = draft.Fit.ToString().ToLowerInvariant(),
                ["dim"] = Math.Round(draft.Dim, 2),
                ["blur"] = Math.Round(draft.Blur),
            };
        }
        else
        {
            foreach (var old in Directory.GetFiles(folder, "background.*"))
                File.Delete(old);
        }

        var text = json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        File.WriteAllText(Path.Combine(folder, ThemeLoader.FileName), text, new UTF8Encoding(false));

        return target;
    }

    /// <summary>Тема одним файлом — чтобы ею поделиться.</summary>
    public static void Export(string id, string zipPath, string? root = null)
    {
        var folder = Path.Combine(root ?? ThemeLoader.DefaultRoot, id);

        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    /// <summary>
    /// Кладёт тему из файла в <c>themes\</c>; возвращает имя папки.
    /// </summary>
    /// <remarks>
    /// Файл чужой. Распаковывается во временную папку и берётся только
    /// то, что там похоже на тему: theme.json в корне. Путь за пределы
    /// папки .NET при распаковке не пустит, а дальше тему проверит
    /// ThemeLoader — как любую другую.
    /// </remarks>
    public static string Import(string zipPath, string? root = null)
    {
        var themes = root ?? ThemeLoader.DefaultRoot;
        var temp = Path.Combine(Path.GetTempPath(), $"netzapret-theme-{Guid.NewGuid():N}");

        try
        {
            ZipFile.ExtractToDirectory(zipPath, temp);

            if (!File.Exists(Path.Combine(temp, ThemeLoader.FileName)))
                throw new InvalidDataException($"В файле нет {ThemeLoader.FileName} — это не тема NetZapret.");

            string name = Path.GetFileNameWithoutExtension(zipPath);

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp, ThemeLoader.FileName)),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (document.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title)
                    name = title;
            }
            catch (JsonException)
            {
                // Имя возьмём из файла; саму тему проверит загрузчик.
            }

            Directory.CreateDirectory(themes);
            var id = Unique(Slug(name), themes);

            Directory.Move(temp, Path.Combine(themes, id));
            return id;
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>Имя папки из названия: допустимые в Windows знаки, без пробелов по краям.</summary>
    internal static string Slug(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => bad.Contains(c) || char.IsWhiteSpace(c) ? '-' : char.ToLowerInvariant(c)).ToArray());

        cleaned = cleaned.Trim('-', '.');

        return cleaned.Length == 0 ? "theme" : cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    private static string Unique(string slug, string root)
    {
        var id = slug;

        for (int i = 2; Directory.Exists(Path.Combine(root, id)) || ThemeLoader.Shipped.Contains(id); i++)
            id = $"{slug}-{i}";

        return id;
    }
}
