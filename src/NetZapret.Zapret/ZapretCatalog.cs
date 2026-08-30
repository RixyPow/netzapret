using Microsoft.Data.Sqlite;

namespace NetZapret.Zapret;

/// <summary>Набор адресов, из которого берутся ответы.</summary>
/// <remarks>
/// Профили — это конкурирующие наборы прокси для одних и тех же имён.
/// «XBOX DNS», «Comss DNS» и прочие отвечают на те же восемьсот доменов
/// разными адресами, и работают они у разных людей по-разному: адрес живёт,
/// пока живёт чужой узел за ним.
/// </remarks>
public sealed record CatalogProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Сколько имён профиль покрывает.</summary>
    public int Answers { get; init; }
}

/// <summary>Сервис в каталоге Zapret.</summary>
public sealed record CatalogService
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary><c>ai</c>, <c>other</c> либо <c>direct</c>.</summary>
    public required string Category { get; init; }

    /// <summary>
    /// <c>dns</c> — адрес берётся из выбранного профиля;
    /// <c>hosts</c> — адрес зашит в каталоге.
    /// </summary>
    /// <remarks>
    /// Различие существенное. Сервису вида <c>dns</c> нужен профиль, и смена
    /// профиля меняет ему адрес; у сервиса вида <c>hosts</c> адрес один
    /// и профиль ему не нужен.
    /// </remarks>
    public required string Kind { get; init; }

    public int Domains { get; init; }
}

/// <summary>
/// Читает каталог сервисов, который Zapret возит с собой.
/// </summary>
/// <remarks>
/// <para>
/// В каталоге лежит то, ради чего люди правят hosts руками: адреса чужих
/// прокси для сервисов, закрывающихся от России самостоятельно. ChatGPT,
/// Spotify, Claude — их не пропускает не оператор, а они сами, и ни десинк,
/// ни туннель тут не помогают; помогает только адрес посредника.
/// </para>
/// <para>
/// Читаем, но не копируем. Восемьсот доменов и семь наборов адресов
/// обновляются вместе с Zapret, а адреса эти живут ровно столько, сколько
/// живёт узел за ними, — копия у нас устарела бы за недели и врала бы
/// молча.
/// </para>
/// <para>
/// И не пишем в hosts. Те же ответы мы отдаём своим резолвером: правка
/// системного файла требует администратора, переживает удаление программы
/// и видна всем приложениям сразу, а наша подстановка снимается вместе
/// с конфигом.
/// </para>
/// </remarks>
public sealed class ZapretCatalog
{
    private readonly string _path;

    private ZapretCatalog(string path) => _path = path;

    /// <summary>Где каталог лежит внутри установки Zapret.</summary>
    public static string RelativePath { get; } = Path.Combine("system", "hosts_catalog.sqlite3");

    /// <summary>
    /// Находит каталог; <c>null</c>, если Zapret не установлен или каталога в нём нет.
    /// </summary>
    /// <remarks>
    /// Отсутствие — не ошибка. Каталог появился в Zapret не сразу, и у части
    /// установок его нет; программа обязана работать и без него, просто без
    /// этого раздела.
    /// </remarks>
    public static ZapretCatalog? Discover(string? zapretRoot = null)
    {
        // Перебираются все известные корни, а не берётся выбранный для
        // движка. Каталог может лежать не там: встроенная копия возит
        // winws2 со списками, а каталог появился позже и в старых сборках
        // его нет — но рядом стоит установка, где он есть.
        foreach (var root in ZapretPaths.Candidates(zapretRoot))
        {
            var path = Path.Combine(root, RelativePath);

            if (File.Exists(path))
                return new ZapretCatalog(path);
        }

        return null;
    }

    private SqliteConnection Open()
    {
        // Только на чтение: файл принадлежит другой программе, и запирать его
        // либо править — не наше дело.
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    public IReadOnlyList<CatalogProfile> Profiles()
    {
        var result = new List<CatalogProfile>();

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();

            command.CommandText = """
                select p.profile_id, p.name, count(a.ip_address)
                from dns_profiles p
                left join dns_answers a on a.profile_id = p.profile_id
                where p.enabled = 1
                group by p.profile_id, p.name, p.sort_order
                order by p.sort_order
                """;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                result.Add(new CatalogProfile
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Answers = reader.GetInt32(2),
                });
            }
        }
        catch (Exception)
        {
            // Схему ведём не мы: она может измениться с обновлением Zapret.
            // Пустой список честнее падения — раздел просто не появится.
        }

        return result;
    }

    public IReadOnlyList<CatalogService> Services()
    {
        var result = new List<CatalogService>();

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();

            command.CommandText = """
                select s.service_id, s.name, s.category, s.kind, count(d.domain_id)
                from services s
                left join domains d on d.service_id = s.service_id
                group by s.service_id, s.name, s.category, s.kind, s.sort_order
                order by s.kind, s.sort_order
                """;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                result.Add(new CatalogService
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Category = reader.GetString(2),
                    Kind = reader.GetString(3),
                    Domains = reader.GetInt32(4),
                });
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>
    /// Адреса для названных сервисов.
    /// </summary>
    /// <param name="serviceIds">Что включено человеком.</param>
    /// <param name="profileId">Какой набор адресов брать для сервисов вида <c>dns</c>.</param>
    /// <remarks>
    /// <para>
    /// Сервисы вида <c>hosts</c> берут адрес из собственной таблицы и профиля
    /// не спрашивают; вида <c>dns</c> — из выбранного профиля. Смешивать их
    /// нельзя: у первых профиля попросту нет, и требование его выбрать
    /// закрыло бы половину каталога ни за чем.
    /// </para>
    /// <para>
    /// При нескольких адресах на имя берётся первый по приоритету — так же,
    /// как поступает hosts, где действует первая совпавшая строка.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> Answers(
        IReadOnlyCollection<string> serviceIds,
        string? profileId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (serviceIds.Count == 0)
            return result;

        try
        {
            using var connection = Open();

            var wanted = string.Join(",", serviceIds.Select((_, i) => $"$s{i}"));

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    select h.hostname, h.ip_address
                    from hosts_entries h
                    join services s on s.service_id = h.service_id
                    where s.kind = 'hosts' and h.service_id in ({wanted})
                    order by h.priority
                    """;

                Bind(command, serviceIds);
                Read(command, result);
            }

            if (profileId is not null)
            {
                using var command = connection.CreateCommand();

                command.CommandText = $"""
                    select d.hostname, a.ip_address
                    from dns_answers a
                    join domains d on d.domain_id = a.domain_id
                    join services s on s.service_id = d.service_id
                    where s.kind = 'dns' and a.profile_id = $p and d.service_id in ({wanted})
                    order by a.priority
                    """;

                command.Parameters.AddWithValue("$p", profileId);
                Bind(command, serviceIds);
                Read(command, result);
            }
        }
        catch (Exception)
        {
        }

        return result;

        static void Bind(SqliteCommand command, IReadOnlyCollection<string> ids)
        {
            int i = 0;

            foreach (var id in ids)
                command.Parameters.AddWithValue($"$s{i++}", id);
        }

        static void Read(SqliteCommand command, Dictionary<string, string> into)
        {
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                // Первый выигрывает: порядок задан приоритетом, и затирать
                // его следующей строкой значит игнорировать этот приоритет.
                into.TryAdd(reader.GetString(0), reader.GetString(1));
            }
        }
    }
}
