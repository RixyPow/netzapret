using System.Net;
using System.Net.Sockets;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Core.Connections;

/// <summary>Что дописано в список программы.</summary>
/// <param name="Program">Программа.</param>
/// <param name="ListPath">
/// Список: домены или адреса (<see cref="ProgramRoutes"/>) — или адреса игры
/// из каталога (<see cref="GameServerLearn"/>).
/// </param>
/// <param name="Value">Дописанное: имя или сеть.</param>
public sealed record ProgramLearned(string Program, string ListPath, string Value)
{
    public bool IsDomain => ListPath.EndsWith(".domains.txt", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Автопополнение маршрутов программ по «Наблюдению» (владелец 13.10).
/// </summary>
/// <remarks>
/// <para>
/// На каждое замеченное соединение программы с включённым автопополнением:
/// имя узнано — оно в список доменов, нет — сеть адреса в список адресов.
/// Имя — как пришло, без сведения к зоне: <c>eu.game.example</c> не тянет
/// за собой весь <c>example</c>, а на адресе CDN имя бывает чужим, и зона
/// увела бы чужие сайты.
/// </para>
/// <para>
/// Адрес — сетью /24 (IPv6 — /64), как голос Discord сетью /20
/// (<see cref="Services.DiscordVoiceLearn"/>): серверы игры стоят кучно,
/// и по одному адресу каждый новый матч был бы промахом и перезапуском.
/// Лишнее в такой сети пойдёт тем же путём и, скорее всего, откроется.
/// Не замерено — выбрано по образцу голоса.
/// </para>
/// <para>
/// Мимо: домашняя сеть, мультикаст, подставные адреса движка без имени
/// (198.18.0.0/15 — по ним не узнать, куда шли). Наблюдение видит только
/// соединения, пока оно идёт: автопополнение без него ничего не дописывает.
/// </para>
/// <para>
/// С 21.10 так же — серверы игр из каталога (<see cref="GameServerLearn"/>):
/// только адреса без имени, в список части игры. Маршрут программы с тем же
/// именем файла важнее — он выбор человека.
/// </para>
/// </remarks>
public sealed class ProgramRouteLearner
{
    /// <summary>Как часто перечитывать настройки и списки — человек мог их поправить.</summary>
    public static readonly TimeSpan Reread = TimeSpan.FromSeconds(15);

    /// <summary>Длина сети IPv4.</summary>
    public const int PrefixV4 = 24;

    /// <summary>Длина сети IPv6.</summary>
    public const int PrefixV6 = 64;

    private static readonly IpCidrRange Fake = IpCidrRange.Parse("198.18.0.0/15");

    private readonly string _settingsPath;
    private readonly string? _root;
    private readonly Func<DateTime> _clock;
    private readonly Func<IReadOnlyList<LearnedGame>>? _games;
    private readonly object _gate = new();

    private Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _readAt = DateTime.MinValue;

    /// <param name="games">
    /// Игры, чьи серверы дописывать сейчас (<see cref="GameServerLearn.Active"/>);
    /// спрашивается при каждом перечитывании. Нет — только маршруты программ.
    /// </param>
    public ProgramRouteLearner(
        string? settingsPath = null,
        string? root = null,
        Func<DateTime>? clock = null,
        Func<IReadOnlyList<LearnedGame>>? games = null)
    {
        _settingsPath = settingsPath ?? AppSettings.DefaultPath;
        _root = root;
        _clock = clock ?? (() => DateTime.UtcNow);
        _games = games;
    }

    private sealed class Known
    {
        public required string Program { get; init; }

        /// <summary>Куда имена; <c>null</c> — имена мимо (игра из каталога).</summary>
        public required string? DomainsList { get; init; }

        public required string AddressesList { get; init; }

        public List<string> Domains { get; } = [];
        public List<IpCidrRange> Networks { get; } = [];
    }

    /// <summary>
    /// Смотрит на соединение и дописывает, если оно новое для программы.
    /// </summary>
    /// <returns>Дописанное; <c>null</c> — ничего.</returns>
    public ProgramLearned? Observe(ConnectionEvent connection)
    {
        if (connection.ExecutableName is not { } name || connection.IsLoopback)
            return null;

        lock (_gate)
        {
            if (_clock() - _readAt >= Reread)
                Read();

            if (!_known.TryGetValue(name, out var known))
                return null;

            if (connection.RemoteAddress is not { } remote)
                return null;

            if (remote.IsIPv4MappedToIPv6)
                remote = remote.MapToIPv4();

            if (LocalNetworks.IsLocal(remote) || WatchContext.IsMulticast(remote))
                return null;

            if (Host(connection.Hostname) is { } host)
            {
                if (known.DomainsList is not { } domains || known.Domains.Any(d => Covers(d, host)))
                    return null;

                ProgramRoutes.Append(domains, host, _root);
                known.Domains.Add(host);

                return new ProgramLearned(known.Program, domains, host);
            }

            if (Fake.Contains(remote) || known.Networks.Any(n => n.Contains(remote)))
                return null;

            var network = Network(remote);
            ProgramRoutes.Append(known.AddressesList, network, _root);
            known.Networks.Add(IpCidrRange.Parse(network));

            return new ProgramLearned(known.Program, known.AddressesList, network);
        }
    }

    /// <summary>Сеть адреса, от её начала: «45.82.160.17» → «45.82.160.0/24».</summary>
    public static string Network(IPAddress address)
    {
        int prefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? PrefixV6 : PrefixV4;
        return AddressInput.Normalize($"{address}/{prefix}") ?? address.ToString();
    }

    /// <summary>Имя без точки в конце и в нижнем регистре; адрес вместо имени — не имя.</summary>
    private static string? Host(string? hostname)
    {
        var host = hostname?.Trim().TrimEnd('.').ToLowerInvariant();

        return string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _) || !host.Contains('.')
            ? null
            : host;
    }

    /// <summary>Строка списка — зона: покрывает и само имя, и его поддомены.</summary>
    private static bool Covers(string zone, string host) =>
        string.Equals(zone, host, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);

    private void Read()
    {
        _readAt = _clock();

        var known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in AppSettings.Load(_settingsPath).ProgramRoutes.Where(p => p.AutoFill))
        {
            var entry = new Known
            {
                Program = route.Program,
                DomainsList = ProgramRoutes.DomainsPath(route.Program),
                AddressesList = ProgramRoutes.AddressesPath(route.Program),
            };

            entry.Domains.AddRange(ProgramRoutes.Entries(entry.DomainsList, _root)
                .Where(d => AddressInput.Normalize(d) is null));

            ReadNetworks(entry);
            known[route.Program] = entry;
        }

        foreach (var game in Games())
        {
            if (known.ContainsKey(game.Program))
                continue;

            var entry = new Known { Program = game.Program, DomainsList = null, AddressesList = game.List };

            ReadNetworks(entry);
            known[game.Program] = entry;
        }

        _known = known;
    }

    /// <summary>Игры к пополнению; не прочитались правила — никаких, до следующего раза.</summary>
    private IReadOnlyList<LearnedGame> Games()
    {
        try
        {
            return _games?.Invoke() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void ReadNetworks(Known entry)
    {
        foreach (var line in ProgramRoutes.Entries(entry.AddressesList, _root))
        {
            if (AddressInput.Normalize(line) is not { } address)
                continue;

            try
            {
                entry.Networks.Add(IpCidrRange.Parse(address));
            }
            catch (RuleConfigurationException)
            {
                // Строка, которую движок не поймёт, — не наша забота здесь.
            }
        }
    }
}
