using NetZapret.Core;

namespace NetZapret.Zapret;

/// <summary>
/// Копия и переименование пресета в папке <c>presets</c>.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 03.11 — дублировать и переименовывать из меню «⋯» у пресета. Пресет
/// зовётся по имени файла (<see cref="ZapretPaths.FindPreset"/>), строка
/// <c># Preset:</c> в шапке правится вместе с ним, чтобы файл не врал о себе.
/// </para>
/// <para>
/// Пресет поставки узнаётся по <c># BuiltinVersion:</c> — он есть у всех
/// в архиве. Такой не переименовывается: обновление вернёт его под прежним
/// именем, и пресетов стало бы два. Копия теряет <c>BuiltinVersion</c> — это уже
/// свой пресет, а не наш, и в список «наших» она не попадает.
/// </para>
/// </remarks>
public static class PresetFiles
{
    private const int MaxName = 80;

    /// <summary>Пресет из поставки: его возвращает обновление.</summary>
    public static bool Shipped(string path) =>
        File.ReadLines(path).TakeWhile(l => l.StartsWith('#') || l.Trim().Length == 0)
            .Any(l => l.StartsWith("# BuiltinVersion:", StringComparison.Ordinal));

    /// <summary>Что не так с именем; <c>null</c> — годится.</summary>
    /// <param name="except">Файл, которому имя можно оставить (переименование в то же).</param>
    public static string? Problem(string? name, string directory, string? except = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "Название не может быть пустым.";

        if (trimmed.Length > MaxName)
            return $"Название длиннее {MaxName} знаков.";

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.EndsWith('.'))
            return "В названии нельзя \\ / : * ? \" < > | и точку в конце.";

        var target = Path.Combine(directory, trimmed + ".txt");

        if (File.Exists(target)
            && !(except is not null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(Path.Combine(directory, except)), StringComparison.OrdinalIgnoreCase)))
        {
            return $"Пресет «{trimmed}» уже есть.";
        }

        return null;
    }

    /// <summary>Имя копии, которого в папке ещё нет: «Копия X», «Копия X (2)»…</summary>
    public static string CopyName(string directory, string name)
    {
        var first = $"Копия {name}";

        if (first.Length > MaxName)
            first = first[..MaxName].TrimEnd();

        var candidate = first;

        for (int n = 2; File.Exists(Path.Combine(directory, candidate + ".txt")); n++)
            candidate = $"{first} ({n})";

        return candidate;
    }

    /// <summary>Копирует пресет под новым именем; возвращает имя файла копии.</summary>
    public static string Duplicate(string directory, string file, string name)
    {
        name = name.Trim();

        if (Problem(name, directory) is { } problem)
            throw new InvalidOperationException(problem);

        var lines = File.ReadAllLines(Path.Combine(directory, file));
        var target = name + ".txt";

        File.WriteAllLines(Path.Combine(directory, target), Retitle(lines, name, dropBuiltin: true));

        return target;
    }

    /// <summary>Переименовывает свой пресет; возвращает новое имя файла.</summary>
    public static string Rename(string directory, string file, string name)
    {
        name = name.Trim();
        var source = Path.Combine(directory, file);

        if (Shipped(source))
            throw new InvalidOperationException("Пресет из поставки обновление вернёт под прежним именем — переименуйте его копию.");

        if (Problem(name, directory, except: file) is { } problem)
            throw new InvalidOperationException(problem);

        var target = name + ".txt";
        var lines = Retitle(File.ReadAllLines(source), name, dropBuiltin: false);

        // Сперва новый файл, потом удаление старого: оборвись запись посреди —
        // пресет не пропадёт. Смена одного регистра — тот же файл в Windows.
        var targetPath = Path.Combine(directory, target);
        bool sameFile = string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);

        if (sameFile)
        {
            var temporary = source + ".rename";
            File.WriteAllLines(temporary, lines);
            File.Delete(source);
            File.Move(temporary, targetPath);
        }
        else
        {
            File.WriteAllLines(targetPath, lines);
            File.Delete(source);
        }

        return target;
    }

    /// <summary>Настройки после переименования: выбранный пресет и подделки секций идут за ним.</summary>
    public static AppSettings Renamed(AppSettings settings, string oldName, string newName) => settings with
    {
        PresetName = string.Equals(settings.PresetName, oldName, StringComparison.OrdinalIgnoreCase) ? newName : settings.PresetName,
        FakeSwaps = settings.FakeSwaps
            .Select(s => string.Equals(s.Preset, oldName, StringComparison.OrdinalIgnoreCase) ? s with { Preset = newName } : s)
            .ToList(),
    };

    /// <summary>Шапка с новым именем: <c># Preset:</c> правится или дописывается первой строкой.</summary>
    internal static IReadOnlyList<string> Retitle(IReadOnlyList<string> lines, string name, bool dropBuiltin)
    {
        var result = new List<string>();
        bool titled = false;
        bool header = true;

        foreach (var line in lines)
        {
            if (header && !line.StartsWith('#') && line.Trim().Length > 0)
                header = false;

            if (header && line.StartsWith("# Preset:", StringComparison.Ordinal))
            {
                result.Add($"# Preset: {name}");
                titled = true;
                continue;
            }

            if (header && dropBuiltin && line.StartsWith("# BuiltinVersion:", StringComparison.Ordinal))
                continue;

            result.Add(line);
        }

        if (!titled)
            result.Insert(0, $"# Preset: {name}");

        return result;
    }
}
