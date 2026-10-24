using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NetZapret.Zapret;

/// <summary>Чем копия файлов Zapret GUI отличается от установки.</summary>
public sealed record ZapretGuiDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<ZapretGuiChange> Changed)
{
    public bool Same => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// Изменившийся файл: строки добавлены и убраны — для текста; у двоичного <see cref="Binary"/>.
/// </summary>
public sealed record ZapretGuiChange(string Path, bool Binary, int LinesAdded, int LinesRemoved);

/// <summary>
/// Своя копия файлов Zapret GUI в репозитории — <c>vendor\zapret-gui</c>.
/// </summary>
/// <remarks>
/// <para>
/// До 22.10 build.cmd и pack.cmd при каждой сборке копировали папки exe,
/// lists, lua, bin и windivert.filter из установки Zapret GUI (C:\Zapret\Dev)
/// как есть. Обновился Zapret GUI — и следующий архив вёз другие модули lua,
/// списки и драйвер, и никто этого не сверял. Владелец считал, что «мы своё
/// всё ведём»; теперь так и есть: сборка берёт копию из репозитория, а копия
/// обновляется только по его слову (<c>nz zapret-gui take</c>) с отчётом,
/// что изменилось, — и изменения видны в git.
/// </para>
/// <para>
/// Копия кладётся побайтно (в .gitattributes она <c>-text</c>): у списков
/// Zapret GUI концы CRLF, а у части шаблонов фильтра последняя строка без
/// перевода, и нормализация концов строк дала бы архив не тот, что у них.
/// </para>
/// </remarks>
public static class ZapretGuiCopy
{
    /// <summary>Папка копии относительно корня рабочей копии.</summary>
    public static readonly string Folder = Path.Combine("vendor", "zapret-gui");

    /// <summary>Откуда и когда снята копия — пишет <see cref="Take"/>.</summary>
    public const string VersionFile = "VERSION.txt";

    /// <summary>
    /// Папки, которые берутся целиком: списки пресетов, модули lua
    /// (<c>--lua-init</c>), образцы пакетов (<c>--blob</c>) и шаблоны фильтра
    /// (<c>--wf-raw-part</c>).
    /// </summary>
    private static readonly string[] Directories = ["lists", "lua", "bin", "windivert.filter"];

    /// <summary>
    /// Отдельные файлы: WinDivert и каталог сервисов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Из exe — только драйвер и его библиотека; прочее там не наше.
    /// winws2.exe — свой, из tools\ (почему — в build.cmd). winws.exe —
    /// прежний движок: наши пресеты водят его через <c>--lua-desync</c>,
    /// которого он не знает. aaaaaaaaa1 побайтно равен Monkey64.sys, и никто
    /// на него не ссылается: WinDivert.dll называет только Monkey64.sys.
    /// stop.bat — Zapret GUI: гасит движки за спиной надзора, а тот их
    /// поднимает. dnscrypt-proxy — DNS-клиент самого Zapret GUI с его
    /// обновления 08.10; у нас его не запускает ничто, 0.14.4 по ошибке
    /// везла 12 МБ.
    /// </para>
    /// <para>
    /// Каталог сервисов — семь наборов адресов посредников для сервисов,
    /// закрывшихся от России; без него у человека без своей установки Zapret
    /// этого раздела нет вовсе. С него же <c>nz catalog</c> снимает
    /// config\catalog.zapret.yaml.
    /// </para>
    /// </remarks>
    private static readonly string[] Files =
    [
        Path.Combine("exe", "Monkey64.sys"),
        Path.Combine("exe", "WinDivert.dll"),
        Path.Combine("system", "hosts_catalog.sqlite3"),
    ];

    /// <summary>
    /// Не берётся никогда: свои списки человека в Zapret GUI.
    /// </summary>
    /// <remarks>
    /// Пресеты и код их не читают, а до 22.10 они уезжали в каждый архив
    /// выпуска — в pre.2 лежал автосписок Zapret GUI владельца. В публичном
    /// git им не место тем более.
    /// </remarks>
    private static readonly string[] Personal = [Path.Combine("lists", "user")];

    /// <summary>Файлы, которые копия берёт у <paramref name="root"/>, — путями от него.</summary>
    public static IReadOnlyList<string> Managed(string root)
    {
        var result = new List<string>();

        foreach (var directory in Directories)
        {
            var path = Path.Combine(root, directory);

            if (!Directory.Exists(path))
                continue;

            result.AddRange(Directory
                .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f))
                .Where(f => !Personal.Any(p => f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))));
        }

        result.AddRange(Files.Where(f => File.Exists(Path.Combine(root, f))));
        result.Sort(StringComparer.OrdinalIgnoreCase);

        return result;
    }

    /// <summary>Что изменилось бы в копии <paramref name="copy"/>, возьми её из <paramref name="install"/>.</summary>
    public static ZapretGuiDiff Compare(string install, string copy)
    {
        var fresh = Managed(install).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var held = Directory.Exists(copy)
            ? Managed(copy).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var changed = new List<ZapretGuiChange>();

        foreach (var file in fresh.Where(held.Contains).Order(StringComparer.OrdinalIgnoreCase))
        {
            var after = File.ReadAllBytes(Path.Combine(install, file));
            var before = File.ReadAllBytes(Path.Combine(copy, file));

            if (after.AsSpan().SequenceEqual(before))
                continue;

            if (IsBinary(after) || IsBinary(before))
            {
                changed.Add(new ZapretGuiChange(file, Binary: true, 0, 0));
                continue;
            }

            var (added, removed) = LineDelta(before, after);
            changed.Add(new ZapretGuiChange(file, Binary: false, added, removed));
        }

        return new ZapretGuiDiff(
            [.. fresh.Where(f => !held.Contains(f)).Order(StringComparer.OrdinalIgnoreCase)],
            [.. held.Where(f => !fresh.Contains(f)).Order(StringComparer.OrdinalIgnoreCase)],
            changed);
    }

    /// <summary>
    /// Снимает копию из <paramref name="install"/>: новое и изменившееся кладёт,
    /// убранное у них — убирает, и пишет <see cref="VersionFile"/>.
    /// </summary>
    /// <returns>Что изменилось — тот же отчёт, что у <see cref="Compare"/> до снятия.</returns>
    public static ZapretGuiDiff Take(string install, string copy, DateTime now)
    {
        var diff = Compare(install, copy);

        foreach (var file in diff.Removed)
            File.Delete(Path.Combine(copy, file));

        foreach (var file in diff.Added.Concat(diff.Changed.Select(c => c.Path)))
        {
            var target = Path.Combine(copy, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(install, file), target, overwrite: true);
        }

        RemoveEmptyDirectories(copy);

        File.WriteAllText(
            Path.Combine(copy, VersionFile),
            $"Zapret GUI {InstalledVersion(install) ?? "— версия не определилась"}\r\n"
                + $"Снято {now:dd.MM.yyyy HH:mm} из {install} командой nz zapret-gui take.\r\n",
            new UTF8Encoding(false));

        return diff;
    }

    /// <summary>
    /// Версия установленного Zapret GUI; <c>null</c> — не нашлась.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сперва — <c>_internal\install_manifest.json</c>: его переписывает
    /// обновление изнутри программы. Деинсталлятор unins000.exe оно не трогает:
    /// 24.10 Zapret GUI сам обновился с 21.1.7.163 до 21.1.7.201 (lua 1.0.6,
    /// winws2 v1.0.6-25), а unins000.exe от 15.10 по-прежнему говорил «163» —
    /// и копия записала бы чужой номер.
    /// </para>
    /// <para>
    /// Деинсталлятор — запасом, для установок без манифеста: сама программа
    /// лежит в _internal (PyInstaller), и версию в свойствах файла несёт только
    /// он (22.10: 21.1.7.163 у установки от 15.10).
    /// </para>
    /// </remarks>
    public static string? InstalledVersion(string install)
    {
        if (ManifestVersion(Path.Combine(install, "_internal", "install_manifest.json")) is { } manifest)
            return manifest;

        var uninstaller = Path.Combine(install, "unins000.exe");

        if (!File.Exists(uninstaller))
            return null;

        var version = FileVersionInfo.GetVersionInfo(uninstaller).ProductVersion?.Trim();

        return string.IsNullOrEmpty(version) ? null : version;
    }

    /// <summary>Поле <c>version</c> манифеста установки; <c>null</c> — нет файла или поля.</summary>
    private static string? ManifestVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == System.Text.Json.JsonValueKind.String
                && version.GetString()?.Trim() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Первая строка <see cref="VersionFile"/> копии — «Zapret GUI 21.1.7.163»; <c>null</c> — копии нет.</summary>
    public static string? CopyVersion(string copy)
    {
        var path = Path.Combine(copy, VersionFile);

        return File.Exists(path) ? File.ReadLines(path).FirstOrDefault() : null;
    }

    /// <summary>Двоичный ли файл: нулевой байт в первых 8 КБ, как считает и git.</summary>
    private static bool IsBinary(byte[] content) =>
        content.AsSpan(0, Math.Min(content.Length, 8000)).Contains((byte)0);

    /// <summary>Сколько строк добавлено и убрано — без учёта порядка и концов строк.</summary>
    private static (int Added, int Removed) LineDelta(byte[] before, byte[] after)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in Lines(before))
            counts[line] = counts.GetValueOrDefault(line) + 1;

        int added = 0;

        foreach (var line in Lines(after))
        {
            if (counts.GetValueOrDefault(line) > 0)
                counts[line]--;
            else
                added++;
        }

        return (added, counts.Values.Sum());
    }

    private static IEnumerable<string> Lines(byte[] content) =>
        Encoding.UTF8.GetString(content)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0);

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }
}
