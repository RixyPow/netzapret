using System.Text.Json;

namespace NetZapret.Core.Themes;

/// <summary>Тема, прочитанная из папки, или то, почему её нельзя применить.</summary>
public sealed record ThemeLoad(string Id, string Folder, Theme? Theme, IReadOnlyList<string> Problems)
{
    public bool Ok => Theme is not null && Problems.Count == 0;
}

/// <summary>
/// Читает темы из папки <c>themes\</c>: по теме на папку, в каждой <c>theme.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Файл — чужой: его пишет художник, а не мы. Отсюда строгость. Путь
/// к картинке и шрифтам — только имя внутри папки темы: ни ссылок в сеть,
/// ни абсолютных путей, ни <c>..</c>. Незнакомый слот цвета — ошибка,
/// а не молча пропущенная строка: опечатка в имени слота иначе выглядела бы
/// как «художник не задал цвет», и тема вышла бы не той, что нарисована.
/// </para>
/// <para>
/// <c>"base": "dark"</c> берёт недостающие цвета и шрифты у встроенной темы,
/// чтобы не заполнять все четырнадцать слотов ради трёх своих. Один уровень:
/// основа сама на другую основу не ссылается.
/// </para>
/// </remarks>
public static class ThemeLoader
{
    public const string FileName = "theme.json";

    /// <summary>Встроенные темы — они же основы для чужих.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["dark", "light"];

    /// <summary>
    /// Темы, которые едут с программой, — в этом порядке в списке выбора.
    /// </summary>
    /// <remarks>
    /// Серая и тёмно-синяя добавлены 24.09 по просьбе владельца. Основами
    /// они не служат: основа — только тёмная и светлая, чтобы чужая тема
    /// не зависела от темы, которую мы однажды поправим.
    /// </remarks>
    public static IReadOnlyList<string> Shipped { get; } = ["dark", "light", "grey", "tinted"];

    public const long MaxImageBytes = 20 * 1024 * 1024;
    public const long MaxFontBytes = 8 * 1024 * 1024;

    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg"];
    private static readonly string[] FontTypes = [".ttf", ".otf"];

    /// <summary>Папка тем у установки.</summary>
    public static string DefaultRoot => Path.GetFullPath("themes");

    /// <summary>Все темы из папки: встроенные первыми, затем по имени.</summary>
    public static IReadOnlyList<ThemeLoad> LoadAll(string? root = null)
    {
        var folder = root ?? DefaultRoot;

        if (!Directory.Exists(folder))
            return [];

        return Directory.GetDirectories(folder)
            .Where(d => File.Exists(Path.Combine(d, FileName)))
            .Select(d => LoadFolder(d, folder))
            .OrderBy(t => Shipped.Contains(t.Id) ? Shipped.ToList().IndexOf(t.Id) : int.MaxValue)
            .ThenBy(t => t.Theme?.Name ?? t.Id, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Одна тема по имени папки.</summary>
    public static ThemeLoad Load(string id, string? root = null) =>
        LoadFolder(Path.Combine(root ?? DefaultRoot, id), root ?? DefaultRoot);

    private static ThemeLoad LoadFolder(string folder, string root)
    {
        var id = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var problems = new List<string>();

        JsonElement json;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, FileName)),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            json = document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return new ThemeLoad(id, folder, null, [$"{FileName} не читается: {ex.GetBaseException().Message}"]);
        }

        // Основа: только встроенная и только одним уровнем.
        Theme? basis = null;

        if (Text(json, "base") is { } baseId)
        {
            if (!BuiltIn.Contains(baseId) || baseId == id)
            {
                problems.Add($"основа «{baseId}» — такой встроенной темы нет; бывают {string.Join(", ", BuiltIn)}");
            }
            else
            {
                var loaded = LoadFolder(Path.Combine(root, baseId), root);

                if (loaded.Theme is null)
                    problems.Add($"основа «{baseId}» не прочиталась: {string.Join("; ", loaded.Problems)}");
                else
                    basis = loaded.Theme;
            }
        }

        var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);

        if (json.TryGetProperty("colors", out var colorsJson) && colorsJson.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in colorsJson.EnumerateObject())
            {
                if (!ThemeSlots.All.Contains(property.Name))
                    problems.Add($"незнакомый цвет «{property.Name}»; слоты: {string.Join(", ", ThemeSlots.All)}");
                else if (!ThemeColor.TryParse(property.Value.GetString(), out var color))
                    problems.Add($"цвет «{property.Name}» = «{property.Value}» — нужно #RRGGBB или #AARRGGBB");
                else
                    colors[property.Name] = color;
            }
        }

        foreach (var slot in ThemeSlots.All)
        {
            if (colors.ContainsKey(slot))
                continue;

            if (basis is not null)
                colors[slot] = basis[slot];
            else
                problems.Add($"нет цвета «{slot}» — задайте его или укажите \"base\": \"dark\"");
        }

        // Текст полупрозрачным быть не может: сквозь него видно то, на чём
        // он стоит, и проверить контраст заранее нельзя.
        foreach (var slot in new[] { ThemeSlots.Text, ThemeSlots.Muted, ThemeSlots.Faint, ThemeSlots.Accent,
                     ThemeSlots.Danger, ThemeSlots.Warn, ThemeSlots.OnAccent, ThemeSlots.Backdrop })
        {
            if (colors.TryGetValue(slot, out var color) && !color.Opaque)
                problems.Add($"цвет «{slot}» должен быть непрозрачным");
        }

        var fontsJson = json.TryGetProperty("fonts", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

        string Font(string key, string fallback) =>
            (fontsJson.ValueKind == JsonValueKind.Object ? Text(fontsJson, key) : null) ?? fallback;

        var ui = Font("ui", basis?.Fonts.Ui ?? "Bahnschrift, Segoe UI");

        var files = new List<string>();

        if (fontsJson.ValueKind == JsonValueKind.Object
            && fontsJson.TryGetProperty("files", out var filesJson) && filesJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesJson.EnumerateArray())
            {
                if (Inside(folder, file.GetString(), FontTypes, MaxFontBytes, "шрифт", problems) is { } path)
                    files.Add(path);
            }
        }

        var fonts = new ThemeFonts
        {
            Ui = ui,
            Mono = Font("mono", basis?.Fonts.Mono ?? "Cascadia Mono, Consolas"),
            Display = Font("display", basis?.Fonts.Display ?? ui),
            Files = files,
        };

        ThemeBackground? background = null;

        if (json.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.Object)
        {
            var image = Inside(folder, Text(bg, "image"), ImageTypes, MaxImageBytes, "картинка фона", problems);

            var fit = BackgroundFit.Cover;

            if (Text(bg, "fit") is { } fitText && !Enum.TryParse(fitText, ignoreCase: true, out fit))
                problems.Add($"укладка фона «{fitText}» — бывает cover, contain или tile");

            double dim = Number(bg, "dim") ?? 0.5;
            double blur = Number(bg, "blur") ?? 24;

            if (dim is < 0 or > 1)
                problems.Add($"затемнение фона {dim} — нужно число от 0 до 1");

            if (blur is < 0 or > 80)
                problems.Add($"размытие фона {blur} — нужно число от 0 до 80");

            if (image is not null)
                background = new ThemeBackground { Image = image, Fit = fit, Dim = Math.Clamp(dim, 0, 1), Blur = Math.Clamp(blur, 0, 80) };
        }

        if (problems.Count > 0 && colors.Count < ThemeSlots.All.Count)
            return new ThemeLoad(id, folder, null, problems);

        var theme = new Theme
        {
            Id = id,
            Name = Text(json, "name") ?? id,
            Author = Text(json, "author"),
            Folder = Path.GetFullPath(folder),
            Colors = colors,
            Fonts = fonts,
            Background = background,
        };

        // Контраст без картинки сверяется здесь; с картинкой — ещё раз в окне,
        // когда яркость картинки измерена.
        foreach (var failure in ThemeContrast.Check(theme))
            problems.Add("не читается: " + failure);

        return new ThemeLoad(id, folder, theme, problems);
    }

    /// <summary>
    /// Полный путь к файлу внутри папки темы; <c>null</c> и запись в проблемы — если нельзя.
    /// </summary>
    private static string? Inside(string folder, string? name, string[] types, long maxBytes, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{what}: имя файла не указано");
            return null;
        }

        if (name.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(name))
        {
            problems.Add($"{what} «{name}»: только файл в папке темы, не ссылка и не путь");
            return null;
        }

        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, name));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{what} «{name}»: выходит за папку темы");
            return null;
        }

        if (!types.Contains(Path.GetExtension(full).ToLowerInvariant()))
        {
            problems.Add($"{what} «{name}»: годится {string.Join(", ", types)}");
            return null;
        }

        var info = new FileInfo(full);

        if (!info.Exists)
        {
            problems.Add($"{what} «{name}»: файла нет в папке темы");
            return null;
        }

        if (info.Length > maxBytes)
        {
            problems.Add($"{what} «{name}»: {info.Length / 1024 / 1024} МБ, предел {maxBytes / 1024 / 1024} МБ");
            return null;
        }

        return full;
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    private static double? Number(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
