using System.Text;

namespace NetZapret.Zapret;

/// <summary>
/// Свой профиль десинка: имена, которым рецепт выбран руками.
/// </summary>
/// <remarks>
/// Нужен затем, что режим <c>desync</c> в правилах до сих пор означал одно —
/// «мимо туннеля». Что с именем сделает winws2, решал пресет, и если имя
/// не попадало ни в один его список, не делалось ничего. Снаружи это
/// неотличимо от неработающего десинка.
/// </remarks>
public sealed record OwnDesyncProfile
{
    public required string Name { get; init; }

    /// <summary>Шаги <c>--lua-desync</c> по порядку, без ключа.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Полный путь к списку имён для этого профиля.</summary>
    public required string HostListPath { get; init; }
}

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
    /// <param name="own">
    /// Свои профили: имена, которым человек выбрал рецепт руками.
    /// </param>
    public static IReadOnlyList<string> Build(
        ZapretPreset preset,
        string? excludeList = null,
        IReadOnlyList<OwnDesyncProfile>? own = null)
    {
        var arguments = new List<string>(preset.GlobalArguments);

        // Свои профили идут перед пресетовскими, и это единственное место,
        // где порядок решает всё: winws2 отдаёт пакет первому профилю,
        // чей фильтр совпал, и дальше не смотрит. Стоя после, наш рецепт
        // никогда бы не сработал на именах, которые пресет уже забрал себе
        // под «пропустить» — а это ровно тот случай, ради которого выбор
        // рецепта и заведён.
        foreach (var profile in own ?? [])
        {
            if (profile.Steps.Count == 0 || string.IsNullOrWhiteSpace(profile.HostListPath))
                continue;

            arguments.Add("--new");
            arguments.Add($"--name=NetZapret: {profile.Name}");
            arguments.Add("--filter-tcp=80,443");
            arguments.Add($"--hostlist={profile.HostListPath}");

            if (!string.IsNullOrWhiteSpace(excludeList))
                arguments.Add($"--hostlist-exclude={excludeList}");

            foreach (var step in profile.Steps)
                arguments.Add($"--lua-desync={step}");
        }

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

    /// <summary>Каталог для списков своих профилей.</summary>
    public static string OwnListsDirectory => Path.Combine("runtime", "desync");

    /// <summary>
    /// Раскладывает имена по рецептам и пишет их списки на диск.
    /// </summary>
    /// <remarks>
    /// Файл на рецепт, а не на имя: у winws2 профиль принимает один список,
    /// и десять имён с одним рецептом должны попасть в один профиль, иначе
    /// их стало бы десять — с десятикратным разбором каждого пакета.
    /// </remarks>
    /// <param name="byRecipe">Рецепт (имя и шаги) → имена, которые им чинить.</param>
    public static IReadOnlyList<OwnDesyncProfile> WriteOwnLists(
        IReadOnlyList<(string Name, IReadOnlyList<string> Steps, IReadOnlyList<string> Domains)> byRecipe,
        string? directory = null)
    {
        var root = directory ?? OwnListsDirectory;
        var profiles = new List<OwnDesyncProfile>();

        try
        {
            Directory.CreateDirectory(root);

            // Прежние списки убираем: рецепт могли переименовать или снять,
            // и осиротевший файл остался бы лежать. Сам по себе он безвреден —
            // на него никто не сошлётся, — но разбираться в каталоге, где
            // половина файлов ничья, придётся уже человеку.
            foreach (var stale in Directory.EnumerateFiles(root, "*.txt"))
                File.Delete(stale);

            foreach (var (name, steps, domains) in byRecipe)
            {
                if (steps.Count == 0 || domains.Count == 0)
                    continue;

                var path = Path.Combine(root, Sanitize(name) + ".txt");

                File.WriteAllText(
                    path,
                    string.Join(Environment.NewLine, domains) + Environment.NewLine,
                    new UTF8Encoding(false));

                profiles.Add(new OwnDesyncProfile
                {
                    Name = name,
                    Steps = steps,
                    HostListPath = Path.GetFullPath(path),
                });
            }
        }
        catch (IOException)
        {
            // Не записалось — работаем без своих профилей: пресет остаётся
            // как был. Хуже, чем задумано, но не хуже, чем до этой возможности.
        }

        return profiles;
    }

    /// <summary>Имя рецепта в имя файла: в нём бывают пробелы и скобки.</summary>
    private static string Sanitize(string name)
    {
        var safe = name.Trim();

        foreach (char bad in Path.GetInvalidFileNameChars())
            safe = safe.Replace(bad, '-');

        return safe.Replace(' ', '-').Replace(":", string.Empty);
    }

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
