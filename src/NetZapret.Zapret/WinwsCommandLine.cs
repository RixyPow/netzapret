namespace NetZapret.Zapret;

/// <summary>
/// Собирает командную строку winws2 из пресета.
/// </summary>
public static class WinwsCommandLine
{
    /// <summary>
    /// Разворачивает пресет в список аргументов запуска.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Аргументы берутся как есть, включая разделители <c>--new</c>: winws2
    /// разбирает их сам, и полагаться на полноту нашей модели при запуске
    /// нельзя. Незнакомый ключ должен дойти до движка нетронутым, иначе
    /// пресет поедет молча и не так, как задумывал автор.
    /// </para>
    /// <para>
    /// Пути к спискам в пресете относительные (<c>lists/youtube.txt</c>),
    /// поэтому процесс обязан запускаться с рабочим каталогом в корне Zapret.
    /// Переписывать их в абсолютные здесь нельзя: тогда сломается перенос
    /// установки, а winws2 всё равно резолвит их сам.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Build(ZapretPreset preset)
    {
        var arguments = new List<string>(preset.GlobalArguments);

        foreach (var section in preset.Sections)
        {
            arguments.Add("--new");
            arguments.AddRange(section.RawArguments);
        }

        return arguments;
    }

    /// <summary>
    /// Проверяет, что все файлы, на которые ссылается пресет, существуют.
    /// </summary>
    /// <returns>Пути, которых не хватает; пустой список — всё на месте.</returns>
    /// <remarks>
    /// Отсутствующий список winws2 переживает молча, просто не сопоставив
    /// ничего в этой секции. Такую поломку лучше увидеть до запуска.
    /// </remarks>
    public static IReadOnlyList<string> FindMissingFiles(ZapretPreset preset, string zapretRoot)
    {
        var missing = new List<string>();

        foreach (var section in preset.Sections)
        {
            foreach (var relative in section.HostListPaths.Concat(section.IpSetPaths))
            {
                var path = Path.Combine(zapretRoot, relative.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(path))
                    missing.Add(relative);
            }
        }

        return missing;
    }
}
