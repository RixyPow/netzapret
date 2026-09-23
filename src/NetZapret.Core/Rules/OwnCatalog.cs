using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetZapret.Core.Rules;

/// <summary>Запись нашего каталога: сервис, его имена и откуда брать адрес.</summary>
public sealed record OwnCatalogEntry
{
    public required string Name { get; init; }

    /// <summary>Имена или зоны; зона покрывает поддомены.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Названные адреса; пусто, если адрес спрашивается.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>
    /// Адрес спросить у честного резолвера в момент закрепления.
    /// </summary>
    /// <remarks>
    /// Предпочтительная форма для сетей доставки. Число стареет: у
    /// <c>image.tmdb.org</c> живой узел сменился трижды за четверо суток,
    /// и вписанный в поставку адрес устарел бы раньше, чем его скачают.
    /// </remarks>
    public bool Resolve { get; init; }

    /// <summary>Покрывает ли запись это имя.</summary>
    public bool Covers(string host)
    {
        foreach (var zone in Names)
        {
            var bare = zone.TrimStart('*', '.');

            if (string.Equals(bare, host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Собственный каталог адресов — то, чего нет у Zapret.
/// </summary>
/// <remarks>
/// <para>
/// Каталог Zapret покрывает 1085 имён, но собран под другую задачу: это
/// наборы чужих публичных прокси против отказа по стране. Ломается же
/// сплошь и рядом другое — заглушка резолвера, оборванная цепочка CNAME,
/// регион, отшитый сетью доставки, — и таких имён там нет вовсе.
/// </para>
/// <para>
/// Отсюда свой файл, куда это дописывается. Он читается при каждом обращении,
/// а не при запуске: правка должна действовать сразу, иначе отладка сводится
/// к перезапускам.
/// </para>
/// </remarks>
public sealed class OwnCatalog
{
    public static string DefaultPath => System.IO.Path.Combine("config", "catalog.yaml");

    /// <summary>
    /// Снимок рабочих записей каталога Zapret — рядом с основным файлом.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельным файлом, а не в catalog.yaml, по двум причинам. Основной
    /// файл правит человек, и при обновлении программы он сохраняется —
    /// снимок же обязан обновляться вместе с ней, иначе посредники в нём
    /// состарятся у всех, кто однажды обновился. И тысяча импортированных
    /// строк не должна топить десяток написанных руками с разбором.
    /// </para>
    /// <para>
    /// Собирается командой <c>nz catalog</c> (CatalogSnapshot): в него идёт
    /// только то, что в момент сборки ответило. Руками не правится —
    /// следующая сборка перепишет.
    /// </para>
    /// </remarks>
    public const string SnapshotFile = "catalog.zapret.yaml";

    public static string SnapshotPath => System.IO.Path.Combine("config", SnapshotFile);

    public IReadOnlyList<OwnCatalogEntry> Services { get; }

    /// <summary>
    /// Посредники — адреса, пробуемые автоподбором для любого имени.
    /// </summary>
    public IReadOnlyList<PinCandidate> Intermediaries { get; }

    /// <summary>Что не разобралось; для показа, а не для падения.</summary>
    public IReadOnlyList<string> Problems { get; }

    private OwnCatalog(
        IReadOnlyList<OwnCatalogEntry> services,
        IReadOnlyList<PinCandidate> intermediaries,
        IReadOnlyList<string> problems)
    {
        Services = services;
        Intermediaries = intermediaries;
        Problems = problems;
    }

    /// <summary>
    /// Читает каталог и снимок рядом с ним.
    /// </summary>
    /// <remarks>
    /// Свой файл идёт первым: записи, написанные руками, при совпадении имён
    /// важнее снятых автоматически.
    /// </remarks>
    public static OwnCatalog Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        var services = new List<OwnCatalogEntry>();
        var intermediaries = new List<PinCandidate>();
        var problems = new List<string>();

        Read(target, services, intermediaries, problems);

        var snapshot = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target) ?? ".", SnapshotFile);

        if (!string.Equals(System.IO.Path.GetFullPath(snapshot), System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            Read(snapshot, services, intermediaries, problems);

        return new OwnCatalog(services, intermediaries, problems);
    }

    private static void Read(
        string target,
        List<OwnCatalogEntry> services,
        List<PinCandidate> intermediaries,
        List<string> problems)
    {
        if (!File.Exists(target))
            return;

        try
        {
            var text = File.ReadAllText(target);

            var document = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Document>(text);

            foreach (var raw in document?.Intermediaries ?? [])
            {
                if (!string.IsNullOrWhiteSpace(raw.Address))
                    intermediaries.Add(new PinCandidate(raw.Address, PinSource.Pool, raw.Source ?? "каталог NetZapret"));
            }

            foreach (var raw in document?.Services ?? [])
            {
                if (string.IsNullOrWhiteSpace(raw.Name) || raw.Names is not { Count: > 0 })
                {
                    problems.Add("запись без имени или без списка имён пропущена");
                    continue;
                }

                // Адрес нужен хоть какой: запись, которая не называет адреса
                // и не велит его спросить, не сделает ничего, а в меню займёт
                // строку и обманет ожидание.
                bool resolve = string.Equals(raw.Resolve, "honest", StringComparison.OrdinalIgnoreCase);

                if (!resolve && raw.Addresses is not { Count: > 0 })
                {
                    problems.Add($"«{raw.Name}»: нет ни addresses, ни resolve: honest — пропущено");
                    continue;
                }

                services.Add(new OwnCatalogEntry
                {
                    Name = raw.Name,
                    Names = raw.Names,
                    Addresses = raw.Addresses ?? [],
                    Resolve = resolve,
                });
            }
        }
        catch (Exception ex)
        {
            // Свой файл правит человек, и опечатка в нём не повод не запуститься.
            problems.Add($"{System.IO.Path.GetFileName(target)} не разобран: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Записывает снимок каталога Zapret.</summary>
    /// <param name="header">Строки шапки: откуда и когда снято, сколько ответило.</param>
    public static void WriteSnapshot(
        string path,
        IEnumerable<OwnCatalogEntry> services,
        IEnumerable<PinCandidate> intermediaries,
        IEnumerable<string> header)
    {
        var text = new System.Text.StringBuilder();

        foreach (var line in header)
            text.AppendLine(line.Length == 0 ? "#" : "# " + line);

        text.AppendLine();
        text.AppendLine("intermediaries:");

        foreach (var pool in intermediaries)
            text.AppendLine($"  - address: {pool.Address}").AppendLine($"    source: {Quote(pool.Label)}");

        text.AppendLine();
        text.AppendLine("services:");

        foreach (var entry in services)
        {
            text.AppendLine($"  - name: {Quote(entry.Name)}");
            text.AppendLine("    names:");

            foreach (var name in entry.Names)
                text.AppendLine($"      - {name}");

            text.AppendLine("    addresses:");

            foreach (var address in entry.Addresses)
                text.AppendLine($"      - {address}");
        }

        var directory = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, text.ToString().Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));

        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Записи, покрывающие хоть одно из имён.</summary>
    public IReadOnlyList<OwnCatalogEntry> For(IEnumerable<string> hosts)
    {
        var names = hosts.Select(h => h.TrimStart('*', '.')).ToList();

        return Services.Where(s => names.Any(s.Covers)).ToList();
    }

    /// <summary>
    /// Адреса, вписанные для этого имени, — кандидаты автоподбора пина.
    /// </summary>
    /// <remarks>
    /// Записи с <c>resolve</c> сюда не попадают: их адрес спрашивается
    /// у честного резолвера, а его автоподбор спрашивает и так.
    /// </remarks>
    public IReadOnlyList<PinCandidate> PinCandidates(string host) =>
        Services
            .Where(s => !s.Resolve && s.Covers(host))
            .SelectMany(s => s.Addresses.Select(a => new PinCandidate(a, PinSource.Own, s.Name)))
            .ToList();

    private sealed class Document
    {
        public List<Entry>? Services { get; set; }

        public List<Intermediary>? Intermediaries { get; set; }
    }

    private sealed class Intermediary
    {
        public string? Address { get; set; }
        public string? Source { get; set; }
    }

    private sealed class Entry
    {
        public string? Name { get; set; }
        public List<string>? Names { get; set; }
        public List<string>? Addresses { get; set; }
        public string? Resolve { get; set; }
    }
}
