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
    /// <param name="excludeList">
    /// Файл имён, которые десинку трогать нельзя. <c>null</c> либо пустой
    /// путь — ключ не добавляется вовсе, и командная строка остаётся ровно
    /// такой, какой её задумал автор пресета.
    /// </param>
    public static IReadOnlyList<string> Build(ZapretPreset preset, string? excludeList = null)
    {
        var arguments = new List<string>(preset.GlobalArguments);

        foreach (var section in preset.Sections)
        {
            arguments.Add("--new");

            // Исключение повторяется в каждой секции намеренно: winws2
            // разбирает --hostlist-exclude в пределах профиля, и один ключ
            // в начале накрыл бы только первый из полутора десятков.
            //
            // Ставится сразу после --new, до аргументов секции: так его
            // не перекроет собственный --hostlist-exclude пресета, если тот
            // однажды появится, — последний в профиле побеждает.
            if (!string.IsNullOrWhiteSpace(excludeList))
                arguments.Add($"--hostlist-exclude={excludeList}");

            arguments.AddRange(section.RawArguments);
        }

        return arguments;
    }

    /// <summary>Где лежит список имён, которые десинку трогать нельзя.</summary>
    public static string DefaultExcludeListPath => Path.Combine("runtime", "desync-exclude.txt");

    /// <summary>
    /// Пишет список исключений; <c>null</c> — исключать нечего.
    /// </summary>
    /// <remarks>
    /// Пустой файл не оставляется: winws2 принял бы его молча, а пустой ключ
    /// в командной строке потом читался бы как «исключения настроены» — тогда
    /// как их нет. Отсутствие файла честнее.
    /// </remarks>
    public static string? WriteExcludeList(IReadOnlyList<string> names, string? path = null)
    {
        var target = Path.GetFullPath(path ?? DefaultExcludeListPath);

        try
        {
            if (names.Count == 0)
            {
                if (File.Exists(target))
                    File.Delete(target);

                return null;
            }

            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(target, names);
            return target;
        }
        catch (Exception)
        {
            // Не записалось — запускаемся без исключений: десинк без них
            // работает как прежде, а отказ от запуска стоил бы дороже.
            return null;
        }
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
