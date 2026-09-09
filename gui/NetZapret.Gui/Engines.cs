using System.IO;

namespace NetZapret.Gui;

/// <summary>
/// Ищет движки рядом с программой.
/// </summary>
/// <remarks>
/// <para>
/// Повторяет <c>EngineLocator</c> консоли: тот <c>internal</c> в
/// <c>NetZapret.Cli</c>, а окно на этот проект не ссылается и не должно —
/// оно обязано работать, когда консольной программы рядом нет вовсе.
/// </para>
/// <para>
/// <c>engines/</c> ищется только рядом с программой, без подъёма вверх:
/// сборка кладёт движок именно туда, а поиск до корня диска подхватывал
/// чужой — обзор состояния в распакованном дистрибутиве однажды показал
/// движок, найденный тремя каталогами выше, в постороннем проекте.
/// </para>
/// <para>
/// <c>tools/</c> — наоборот, только с подъёмом: при отладке программа
/// запускается из <c>bin\Debug\net8.0-windows</c>, а движки лежат в корне
/// проекта, четырьмя уровнями выше.
/// </para>
/// </remarks>
internal static class Engines
{
    public static string? FindSingBox() => Find("sing-box.exe");

    private static string? Find(string fileName)
    {
        if (FindIn(Path.Combine(AppContext.BaseDirectory, "engines"), fileName) is { } bundled)
            return bundled;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (FindIn(Path.Combine(directory.FullName, "tools"), fileName) is { } inTools)
                return inTools;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindIn(string root, string fileName)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            // Каталог по пути наверх может оказаться чужим и закрытым.
            // Это не повод не найти движок в следующем.
            return null;
        }
    }
}
