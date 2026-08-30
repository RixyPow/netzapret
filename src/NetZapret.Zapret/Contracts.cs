namespace NetZapret.Zapret;

/// <summary>
/// Управляет процессом winws2.
/// </summary>
/// <remarks>
/// Появился, когда задача сменилась с «ужиться с Zapret GUI» на «заменить его».
/// По форме это тот же супервизор, что и у sing-box: запустить с аргументами,
/// следить за живостью, остановить.
/// </remarks>
public interface IZapretOrchestrator : IAsyncDisposable
{
    ZapretState State { get; }

    /// <summary>Активный пресет либо <c>null</c>, если десинк выключен.</summary>
    ZapretPreset? ActivePreset { get; }

    Task StartAsync(ZapretPreset preset, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public enum ZapretState
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

/// <summary>
/// Расположение установки Zapret.
/// </summary>
public sealed record ZapretPaths
{
    /// <summary>Корень установки; относительно него заданы пути списков в пресетах.</summary>
    public required string Root { get; init; }

    public string PresetDirectory => Path.Combine(Root, "presets", "winws2");

    /// <summary>
    /// Встроенные пресеты Zapret, которые мы показываем наравне со своими.
    /// </summary>
    /// <remarks>
    /// Поимённо, а не всей папкой. В <c>presets/winws2_builtin</c> лежит
    /// больше сотни файлов — почти всё это переборные варианты одной и той же
    /// стратегии, и вывалить их в выбор значило бы утопить в них те два
    /// десятка, между которыми человек действительно выбирает. Здесь только
    /// названные: игровой набор, который иначе взять неоткуда.
    /// </remarks>
    public static IReadOnlyList<string> BuiltinPresetNames { get; } =
    [
        "Default v1 (game filter)",
        "Default v2 (game filter)",
        "Default v3 (game filter)",
        "Default v4 (game filter)",
        "Default v5 (game filter)",
    ];

    /// <summary>
    /// Все файлы пресетов, отовсюду, где они могут лежать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Из всех корней, а не из выбранного. Поставка возит свои шесть пресетов,
    /// и пока читался только первый подходящий корень, установка человека
    /// с двумя десятками собственных была не видна вовсе — положенный туда
    /// пресет не появлялся, сколько программу ни перезапускай.
    /// </para>
    /// <para>
    /// Список собирается заново при каждом обращении: пресеты добавляют
    /// на ходу, и держать их в памяти значило бы требовать перезапуска там,
    /// где достаточно вернуться в меню.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> PresetFiles
    {
        get
        {
            var files = new List<string>();

            // Свой корень первым, остальные следом. Порядок решает при поиске
            // по имени: указавший корень явно вправе получить пресет оттуда,
            // а не одноимённый из соседней установки.
            var roots = new List<string> { Root };
            roots.AddRange(Candidates().Where(r => !PathsEqual(r, Root)));

            foreach (var root in roots)
            {
                var directory = Path.Combine(root, "presets", "winws2");

                if (Directory.Exists(directory))
                    files.AddRange(Directory.EnumerateFiles(directory, "*.txt").OrderBy(p => p));
            }

            foreach (var root in roots)
            {
                var builtin = Path.Combine(root, "presets", "winws2_builtin");

                foreach (var name in BuiltinPresetNames)
                {
                    var path = Path.Combine(builtin, name + ".txt");

                    if (File.Exists(path))
                        files.Add(path);
                }
            }

            return files;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Каталоги пресетов, какие нашлись, в порядке предпочтения.
    /// </summary>
    /// <remarks>
    /// Первым идёт тот, что рядом с программой, — там лежит поставляемый
    /// набор. Дальше установка Zapret, где человек держит свои. Одноимённые
    /// файлы не сливаются: <c>PresetReader</c> отсеивает повторы по пути,
    /// а не по названию, и два разных файла остаются двумя пресетами.
    /// </remarks>
    public static IEnumerable<string> PresetDirectories =>
        Candidates().Select(root => Path.Combine(root, "presets", "winws2"));

    /// <summary>
    /// Куда человеку класть свои пресеты.
    /// </summary>
    /// <remarks>
    /// Установка впереди поставки: она переживает обновление программы,
    /// а каталог рядом с exe при распаковке нового архива перезаписывается —
    /// положенный туда пресет однажды пропадёт без объяснения.
    /// </remarks>
    public static string? WritablePresetDirectory =>
        PresetDirectories
            .Where(Directory.Exists)
            .OrderByDescending(d => !d.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    public string ListDirectory => Path.Combine(Root, "lists");

    /// <summary>
    /// Путь к winws2.exe.
    /// </summary>
    /// <remarks>
    /// Движок лежит в подкаталоге <c>exe</c> рядом с WinDivert.dll и cygwin1.dll,
    /// а не в корне установки. Рабочим каталогом при запуске всё равно должен
    /// быть <see cref="Root"/>: пути к спискам в пресетах заданы относительно него.
    /// </remarks>
    public string ExecutablePath => Path.Combine(Root, "exe", "winws2.exe");

    /// <summary>
    /// Находит файл пресета по названию.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Точное совпадение имеет приоритет над подстрокой. Иначе запрос
    /// «Universal V6» уводит на «Universal V6 voicefix»: тот сортируется
    /// раньше, потому что пробел идёт до точки. Пользователь при этом
    /// получает не тот десинк, о котором просил, и замечает не сразу.
    /// </para>
    /// <para>
    /// Ищется по всем известным установкам, но свой корень идёт первым:
    /// показывать пресеты отовсюду полезно, а вот подсунуть одноимённый
    /// файл из чужой установки тому, кто корень указал явно, — нет.
    /// </para>
    /// </remarks>
    public string? FindPreset(string name)
    {
        var wanted = name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;

        // Корень за корнем, а не по общему списку. Иначе точное совпадение
        // из соседней установки побеждает приблизительное из своей — то есть
        // указавший корень явно получает файл не оттуда, куда показывал.
        foreach (var group in PresetFiles.GroupBy(RootOf))
        {
            if (Match(group, wanted) is { } found)
                return found;
        }

        return null;

        static string? Match(IEnumerable<string> files, string wanted)
        {
            var list = files.ToList();

            var exact = list.FirstOrDefault(p => string.Equals(
                Path.GetFileNameWithoutExtension(p), wanted, StringComparison.OrdinalIgnoreCase));

            return exact ?? list
                .Where(p => Path.GetFileNameWithoutExtension(p)
                    .Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileNameWithoutExtension(p).Length)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Корень установки, которому принадлежит файл пресета.
    /// </summary>
    /// <remarks>
    /// Два шага вверх: пресеты лежат в <c>presets\winws2</c> либо
    /// <c>presets\winws2_builtin</c>, и оба под самим корнем.
    /// </remarks>
    private static string RootOf(string presetPath) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(presetPath))) ?? string.Empty;

    /// <summary>
    /// Ищет Zapret: сперва встроенную копию, затем установку в системе.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Встроенная копия впереди намеренно. Она кладётся рядом с программой
    /// при сборке и версионируется вместе с ней, тогда как установка в системе
    /// живёт своей жизнью: обновилась — и пресет, на который мы ссылаемся,
    /// может измениться или исчезнуть, а выяснится это при запуске.
    /// </para>
    /// <para>
    /// Установка при этом остаётся запасным вариантом: собранная из исходников
    /// копия без шага упаковки движков её не имеет, и без этого отката
    /// программа у разработчика просто не находила бы десинк.
    /// </para>
    /// </remarks>
    public static ZapretPaths? Discover(string? hint = null)
    {
        foreach (var candidate in Candidates(hint))
        {
            if (Directory.Exists(Path.Combine(candidate, "presets", "winws2")))
                return new ZapretPaths { Root = candidate };
        }

        return null;
    }

    /// <summary>
    /// Все места, где может лежать Zapret, в порядке предпочтения.
    /// </summary>
    /// <remarks>
    /// Перечислением, а не одним ответом: части установки может не быть
    /// во встроенной копии. Каталог сервисов, например, в неё до сих пор
    /// не клался, и поиск, остановившийся на первом подходящем корне,
    /// объявлял каталог отсутствующим при живом рядом.
    /// </remarks>
    public static IEnumerable<string> Candidates(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint))
            yield return hint!;

        foreach (var bundled in BundledRoots())
            yield return bundled;

        yield return @"C:\Zapret\Dev";
        yield return @"C:\Zapret";
    }

    /// <summary>Каталог встроенных движков — <c>engines/</c> рядом с программой.</summary>
    public const string BundleDirectory = "engines";

    /// <summary>
    /// Расположение встроенной копии — только рядом с программой.
    /// </summary>
    /// <remarks>
    /// Без подъёма по каталогам: сборка кладёт движки ровно сюда, а поиск
    /// вверх до корня диска подхватывал бы посторонние каталоги с таким же
    /// именем — и молча запускал бы чужой winws2 вместо своего.
    /// </remarks>
    private static IEnumerable<string> BundledRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, BundleDirectory, "zapret");
    }
}
