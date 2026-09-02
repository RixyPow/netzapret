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

    public IReadOnlyList<OwnCatalogEntry> Services { get; }

    /// <summary>Что не разобралось; для показа, а не для падения.</summary>
    public IReadOnlyList<string> Problems { get; }

    private OwnCatalog(IReadOnlyList<OwnCatalogEntry> services, IReadOnlyList<string> problems)
    {
        Services = services;
        Problems = problems;
    }

    public static OwnCatalog Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        var problems = new List<string>();

        if (!File.Exists(target))
            return new OwnCatalog([], problems);

        try
        {
            var text = File.ReadAllText(target);

            var document = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Document>(text);

            var services = new List<OwnCatalogEntry>();

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

            return new OwnCatalog(services, problems);
        }
        catch (Exception ex)
        {
            // Свой файл правит человек, и опечатка в нём не повод не запуститься.
            problems.Add($"каталог не разобран: {ex.GetBaseException().Message}");
            return new OwnCatalog([], problems);
        }
    }

    /// <summary>Записи, покрывающие хоть одно из имён.</summary>
    public IReadOnlyList<OwnCatalogEntry> For(IEnumerable<string> hosts)
    {
        var names = hosts.Select(h => h.TrimStart('*', '.')).ToList();

        return Services.Where(s => names.Any(s.Covers)).ToList();
    }

    private sealed class Document
    {
        public List<Entry>? Services { get; set; }
    }

    private sealed class Entry
    {
        public string? Name { get; set; }
        public List<string>? Names { get; set; }
        public List<string>? Addresses { get; set; }
        public string? Resolve { get; set; }
    }
}
