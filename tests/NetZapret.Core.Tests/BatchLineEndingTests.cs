using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Батники рабочей копии — с CRLF.
/// </summary>
/// <remarks>
/// <para>
/// 23.09 build.cmd рассыпался на обрывки: «'fterwards' is not recognized»
/// и дальше полсотни таких. У cmd с концами LF не находится метка
/// по goto, и он исполняет куски строк как команды. Пока движки были
/// подняты, скрипт шёл веткой без goto и проходил — сломался в тот
/// вечер, когда их остановили.
/// </para>
/// <para>
/// В хранилище всё верно: .gitattributes требует CRLF, и git отдаёт его
/// при выписывании. Ломает правка инструментом, пишущим LF, — так пишет
/// Claude, — после чего рабочая копия так и остаётся с LF, а git status
/// молчит: при записи в хранилище концы всё равно нормализуются.
/// </para>
/// <para>
/// Лечится выписыванием заново: удалить файл и git checkout -- файл.
/// </para>
/// </remarks>
public sealed class BatchLineEndingTests
{
    private static string? Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;
        }

        return null;
    }

    [Fact]
    public void Every_batch_file_has_crlf()
    {
        var root = Root();
        if (root is null)
            return;

        // Свои батники, без чужих и собранного: tools — сторонние
        // программы, build и dist — выкладка, bin и obj — сборка.
        var skip = new[] { "tools", "build", "dist", "bin", "obj", ".git" };

        var broken = Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => skip.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Where(HasBareLf)
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(broken.Count == 0,
            "С концами LF (cmd рассыплет их на обрывки): " + string.Join(", ", broken)
            + ". Выписать заново: удалить и git checkout -- <файл>.");
    }

    /// <summary>
    /// Батники — только ASCII.
    /// </summary>
    /// <remarks>
    /// cmd.exe читает их в кодовой странице OEM, и кириллица в UTF-8
    /// распадается на мусор, а внутри команды — на ложные команды: так
    /// однажды лёг release.cmd. Правило стояло в шапках скриптов словами,
    /// и 23.09 в build.cmd всё равно нашлись два русских блока сообщений.
    /// </remarks>
    [Fact]
    public void Every_batch_file_is_ascii()
    {
        var root = Root();
        if (root is null)
            return;

        var skip = new[] { "tools", "build", "dist", "bin", "obj", ".git" };

        var broken = Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => skip.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Where(f => File.ReadAllBytes(f).Any(b => b > 0x7F))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(broken.Count == 0,
            "Не ASCII (cmd прочтёт в OEM и рассыплет): " + string.Join(", ", broken)
            + ". Русский текст для сообщений — в отдельный файл UTF-8, как docs\\release-notes.footer.md.");
    }

    private static bool HasBareLf(string path)
    {
        var bytes = File.ReadAllBytes(path);

        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '\n' && (i == 0 || bytes[i - 1] != '\r'))
                return true;
        }

        return false;
    }
}
