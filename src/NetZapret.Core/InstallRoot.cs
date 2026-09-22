namespace NetZapret.Core;

/// <summary>
/// Где лежит установка: каталог, от которого считаются все пути.
/// </summary>
/// <remarks>
/// <para>
/// Пути к правилам, настройкам и <c>runtime\</c> заданы относительно
/// рабочего каталога, а тот зависит от способа запуска. В дистрибутиве
/// <c>config\</c> лежит рядом с программой, в рабочей копии — уровнем выше
/// <c>build\</c>.
/// </para>
/// <para>
/// Признак — файл <c>config\rules.yaml</c>, а не папка <c>config</c>,
/// и это исправление 23.09 (issue #3). Программа требует прав
/// администратора, а запущенная с повышением получает рабочим каталогом
/// <c>C:\Windows\System32</c> — где папка <c>config</c> есть всегда:
/// там лежат кусты реестра. Прежняя проверка принимала System32 за свою
/// установку, дальше не искала, и окно просило <c>rules.yaml</c>,
/// а подписка собиралась в ноль серверов. Так было и в 0.6.4.
/// </para>
/// </remarks>
public static class InstallRoot
{
    /// <summary>Файл, по которому установка узнаёт себя.</summary>
    public static readonly string Marker = Path.Combine("config", "rules.yaml");

    /// <summary>Установка ли это.</summary>
    public static bool Is(string directory) =>
        File.Exists(Path.Combine(directory, Marker));

    /// <summary>
    /// Каталог установки: текущий, если он свой, иначе ближайший вверх
    /// от программы; <c>null</c> — не найден.
    /// </summary>
    /// <remarks>
    /// Текущий спрашивается первым: запуск из своего каталога со своим
    /// набором правил — законный случай, и уводить из него нельзя.
    /// </remarks>
    public static string? Find(string current, string programDirectory)
    {
        if (Is(current))
            return Path.GetFullPath(current);

        for (var directory = new DirectoryInfo(programDirectory); directory is not null; directory = directory.Parent)
        {
            if (Is(directory.FullName))
                return directory.FullName;
        }

        return null;
    }

    /// <summary>
    /// Делает каталог установки рабочим; не найден — оставляет как есть.
    /// </summary>
    public static void MoveTo()
    {
        try
        {
            if (Find(Directory.GetCurrentDirectory(), AppContext.BaseDirectory) is { } root)
                Directory.SetCurrentDirectory(root);
        }
        catch (Exception)
        {
            // Не вышло — остаёмся где были: разделы и команды сами скажут,
            // чего им не хватает.
        }
    }
}
