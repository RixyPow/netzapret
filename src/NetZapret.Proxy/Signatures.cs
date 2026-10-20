using System.Net;

namespace NetZapret.Proxy;

/// <summary>
/// Почерк поломки: признак в замере, по которому узнаётся причина, и что с ней делать.
/// </summary>
/// <remarks>
/// Вердикт проверки (<see cref="BlockKind"/>) говорит про одно имя. Почерк
/// смотрит шире — на число байт до обрыва, на адрес из hosts, на то, что
/// несколько имён застряли на одном узле, — и называет то, что одно имя
/// не покажет. Каждый записан по живому случаю, разбор которого стоил вечера:
/// записанный почерк экономит этот вечер следующему.
/// </remarks>
public sealed record Signature
{
    /// <summary>Постоянное имя для тестов и отчётов.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Что это значит.</summary>
    public required string Meaning { get; init; }

    /// <summary>Что с этим делать.</summary>
    public required string Remedy { get; init; }

    /// <summary>Откуда почерк известен: случай и замер.</summary>
    public required string Origin { get; init; }
}

/// <summary>Почерк, найденный в одном прогоне проверки.</summary>
public sealed record SignatureFinding
{
    public required Signature Signature { get; init; }

    /// <summary>Заголовок с подробностью: «Пин в hosts не работает: 72.56.93.144».</summary>
    public required string Headline { get; init; }

    /// <summary>Имена, у которых почерк найден.</summary>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>По строке на имя: имя и чем именно оно подошло.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Сведения, а не поломка: сайт работает, но стоит знать.</summary>
    public bool Calm { get; init; }

    /// <summary>Текст под заголовком — для окна и файла отчёта.</summary>
    public string Describe(int shown = 8) =>
        string.Join("\n", Lines.Take(shown))
        + (Lines.Count > shown ? $"\n… и ещё {Lines.Count - shown}" : string.Empty)
        + $"\n\n{Signature.Meaning}\n\nЧто делать: {Signature.Remedy}\n\nПочерк известен: {Signature.Origin}";
}

/// <summary>
/// Каталог почерков и их поиск в результатах проверки блокировок.
/// </summary>
/// <remarks>
/// Нашёл новый почерк — дописать сюда, с признаком и случаем, по которому
/// он узнан (идея №7 в docs/ideas.md, владелец 14.10: «делай»).
/// </remarks>
public static class Signatures
{
    /// <summary>Сколько имён нужно, чтобы доля «не ответили» что-то значила.</summary>
    public const int MassFloor = 10;

    /// <summary>Пределы заморозки ТСПУ, байт.</summary>
    /// <remarks>
    /// Замеры: 14 381 Б у steamcommunity.com, 13 505 Б у skinsrestorer.net,
    /// ~16 КБ у списков отзыва Let's Encrypt (28.09). С запасом в обе стороны.
    /// 26 011 Б у itch.io и x.ai (20.09) — теснота самой проверки, не заморозка:
    /// в тишине оба дочитались.
    /// </remarks>
    public const int FreezeFrom = 12_000;

    public const int FreezeTo = 20_000;

    /// <summary>Сколько закрытых имён на одном адресе — уже узел, а не совпадение.</summary>
    public const int NodeFloor = 3;

    public static Signature MassFailure { get; } = new()
    {
        Id = "mass-failure",
        Title = "Больше половины имён не ответили вовсе",
        Meaning = "Блокируют выборочно, а не всё подряд. Когда соединение не встаёт с большинством имён, "
            + "так выглядит сбой сети, заминка роутера или теснота самой проверки, а не новые блокировки. "
            + "Другие почерки в такую минуту не ищутся: они бы врали.",
        Remedy = "ничего не менять по этой проверке. Убедиться, что интернет вообще есть, и повторить через пару минут.",
        Origin = "29.09 автозамена пинов приняла такую минуту за смерть 68 адресов и «заменила» живые адреса Spotify.",
    };

    public static Signature DeadPin { get; } = new()
    {
        Id = "dead-pin",
        Title = "Пин в hosts не работает",
        Meaning = "Имя прибито в файле hosts, и проба шла именно на этот адрес — а он сайт не открывает: молчит, "
            + "рвёт рукопожатие, отдаёт чужой сертификат или отказ. Запись hosts сильнее любого маршрута: "
            + "ни десинк, ни VPN это имя не вылечат, пока строка стоит.",
        Remedy = "свой пин — снять или «Проверить актуальные адреса» на вкладке «Файл hosts». Чужую строку "
            + "(от Zapret GUI или другой программы) — удалить в самом hosts или в той программе.",
        Origin = "сентябрь — ChatGPT не открывался ничем: 670 имён в hosts прибиты к давно молчащему прокси; "
            + "октябрь — 531 чужая строка на прокси, который отдаёт сертификат другого сайта.",
    };

    public static Signature Freeze { get; } = new()
    {
        Id = "freeze-16k",
        Title = $"Поток замер на {FreezeFrom / 1000}–{FreezeTo / 1000} КБ",
        Meaning = "Рукопожатие прошло, данные пошли — и встали на первых полутора десятках килобайт. "
            + "Так ТСПУ замораживает соединение, опознанное по рукопожатию: опознаёт в начале, режет позже. "
            + "Медленная страница встаёт где придётся, а не в этих пределах.",
        Remedy = "другой рецепт десинка для этого имени в «Маршрутах» — сорванное опознание отменяет "
            + "и заморозку. Не помог — VPN.",
        Origin = "steamcommunity.com — 14 381 Б три раза из трёх; skinsrestorer.net — 13 505 Б, вылечен "
            + "рецептом на ClientHello; списки отзыва Let's Encrypt — около 16 КБ (28.09).",
    };

    public static Signature SharedNode { get; } = new()
    {
        Id = "shared-node",
        Title = "Закрыт общий узел",
        Meaning = "Несколько закрытых имён стоят за одним адресом, и проба застревала на нём у всех. "
            + "Решает узел, а не имя: обычно это адрес сети доставки (Cloudflare и подобные), а секция "
            + "пресета по адресам забирает такие соединения раньше секций по именам.",
        Remedy = "посмотреть, какая секция берёт адрес: nz where <адрес>. Помогает рецепт этой секции или "
            + "VPN для этих имён; рецепт одного имени тут обычно не помогает.",
        Origin = "03.10 — секции по адресам («discord.com (IP fallback)», «AnyDesk TCP») забирали 22 % имён "
            + "«Russia blacklist» раньше их секций по именам; 14.10 — fandom.com на 172.66.2.166 брала секция Twitter.",
    };

    public static Signature PartlySilent { get; } = new()
    {
        Id = "partly-silent",
        Title = "Отвечают не все адреса имени",
        Meaning = "У имени несколько адресов, и часть молчит. Проба нашла отвечающий, браузер тоже найдёт — "
            + "но после ожидания, поэтому сайт открывается «через раз» или с задержкой.",
        Remedy = "если сайт тормозит на открытии — nz where <молчащий адрес>: его может забирать секция пресета "
            + "по адресам. Иначе — пин на отвечающий адрес.",
        Origin = "05.10 — два фронта Google (142.251.142.206, 173.194.222.100) при поднятых движках "
            + "не принимали соединение, третий отвечал.",
    };

    public static Signature EchHidden { get; } = new()
    {
        Id = "ech-hidden",
        Title = "В браузере с ECH откроется",
        Meaning = "Имя закрыто по рукопожатию с настоящим именем, а приветствие с общим именем ECH из его "
            + "записи HTTPS тот же адрес принимает. Браузер с включённым защищённым DNS шлёт именно "
            + "общее имя, настоящее — зашифрованным, и провайдер его не видит. Проба ECH не шлёт, "
            + "поэтому её вердикт — о браузере без ECH.",
        Remedy = "рецепт десинка этому имени нужен и на общее имя ECH (обычно cloudflare-ech.com): "
            + "его-то провайдер и видит. Без десинка — включить в браузере защищённый DNS (DoH): "
            + "с ним браузер получает запись HTTPS и шлёт ECH.",
        Origin = "ntc.party, zapret-Checker (№88–98, 08.2026): lostfilm.tv открылся, когда cloudflare-ech.com "
            + "добавили в список стратегии; проверка рецепта — curl --ech hard (TesterTi, №102).",
    };

    /// <summary>Все почерки — в том порядке, в каком их показывают.</summary>
    public static IReadOnlyList<Signature> All { get; } = [MassFailure, DeadPin, Freeze, SharedNode, PartlySilent, EchHidden];

    /// <summary>
    /// Почерки в результатах одного прогона.
    /// </summary>
    /// <param name="hosts">Файл hosts целиком (<see cref="HostsFile.Read"/>); <c>null</c> — не читали.</param>
    public static IReadOnlyList<SignatureFinding> Find(
        IReadOnlyList<TargetReport> reports,
        IReadOnlyDictionary<string, List<IPAddress>>? hosts = null)
    {
        // Мимо туннеля: через туннель адрес подменный, и узел, пин или байты
        // говорят о трубе, а не о сайте.
        var direct = reports.Where(r => !r.Tunnelled).ToList();

        // Сперва — не легла ли сеть. Если легла, остальное не ищется вовсе:
        // мёртвыми выглядели бы и живые пины, и живые узлы.
        int silentAll = direct.Count(r => r.Kind is BlockKind.Full or BlockKind.HttpsPort);

        if (direct.Count >= MassFloor && silentAll * 2 > direct.Count)
        {
            var mute = direct.Where(r => r.Kind is BlockKind.Full or BlockKind.HttpsPort).ToList();

            return
            [
                new SignatureFinding
                {
                    Signature = MassFailure,
                    Headline = $"{MassFailure.Title}: {silentAll} из {direct.Count}",
                    Hosts = mute.Select(r => r.Host).ToList(),
                    Lines = [string.Join(", ", mute.Select(r => r.Host).Take(12)) + (mute.Count > 12 ? " …" : "")],
                },
            ];
        }

        var found = new List<SignatureFinding>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        found.AddRange(DeadPins(direct, hosts, taken));
        found.AddRange(Freezes(direct));
        found.AddRange(SharedNodes(direct, taken));
        found.AddRange(PartlySilentNames(direct));
        found.AddRange(EchHiddenNames(direct));

        return found;
    }

    private static IEnumerable<SignatureFinding> EchHiddenNames(List<TargetReport> reports)
    {
        var hidden = reports.Where(r => r.EchName is not null && r.EchHelloAccepted).ToList();

        if (hidden.Count == 0)
            yield break;

        yield return new SignatureFinding
        {
            Signature = EchHidden,
            Headline = EchHidden.Title,
            Hosts = hidden.Select(r => r.Host).ToList(),
            Lines = hidden.Select(r => $"{r.Host} — {Verdict(r)}, с именем {r.EchName} приветствие принято").ToList(),
            Calm = true,
        };
    }

    private static IEnumerable<SignatureFinding> DeadPins(
        List<TargetReport> reports,
        IReadOnlyDictionary<string, List<IPAddress>>? hosts,
        HashSet<string> taken)
    {
        if (hosts is null || hosts.Count == 0)
            yield break;

        var hits = new List<(string Address, TargetReport Report)>();

        foreach (var report in reports)
        {
            bool broken = report.Actionable || report.Spoof.Verdict == DnsVerdict.ForeignCertificate;

            if (!broken || PinOf(hosts, report.Host) is not { Count: > 0 } pinned)
                continue;

            // Проба шла на прибитый адрес — иначе судить о пине не по чему.
            var texts = pinned.Select(a => a.ToString()).ToList();
            var hit = report.Answered is { } answered && texts.Contains(answered)
                ? answered
                : report.Addresses.FirstOrDefault(texts.Contains);

            if (hit is not null)
                hits.Add((hit, report));
        }

        foreach (var group in hits.GroupBy(h => h.Address))
        {
            foreach (var (_, report) in group)
                taken.Add(report.Host);

            yield return new SignatureFinding
            {
                Signature = DeadPin,
                Headline = $"{DeadPin.Title}: {group.Key}",
                Hosts = group.Select(h => h.Report.Host).ToList(),
                Lines = group.Select(h => $"{h.Report.Host} — {Verdict(h.Report)}").ToList(),
            };
        }
    }

    private static List<IPAddress>? PinOf(IReadOnlyDictionary<string, List<IPAddress>> hosts, string host)
    {
        if (hosts.TryGetValue(host, out var pinned))
            return pinned;

        // Словарь мог прийти без сравнения без учёта регистра.
        foreach (var (name, addresses) in hosts)
        {
            if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase))
                return addresses;
        }

        return null;
    }

    private static IEnumerable<SignatureFinding> Freezes(List<TargetReport> reports)
    {
        var frozen = reports
            .Where(r => r.Kind == BlockKind.Stall && r.Data.Received is >= FreezeFrom and <= FreezeTo)
            .ToList();

        if (frozen.Count == 0)
            yield break;

        yield return new SignatureFinding
        {
            Signature = Freeze,
            Headline = Freeze.Title,
            Hosts = frozen.Select(r => r.Host).ToList(),
            Lines = frozen.Select(r => $"{r.Host} — {r.Data.Received:N0} Б").ToList(),
        };
    }

    private static IEnumerable<SignatureFinding> SharedNodes(List<TargetReport> reports, HashSet<string> taken)
    {
        var groups = reports
            .Where(r => r.Kind is BlockKind.Full or BlockKind.HttpsPort or BlockKind.TlsDpi or BlockKind.Stall
                && !taken.Contains(r.Host))
            .Select(r => (Node: r.Answered ?? r.Addresses.FirstOrDefault(), Report: r))
            .Where(x => x.Node is not null)
            .GroupBy(x => x.Node!)
            .Where(g => g.Select(x => x.Report.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= NodeFloor)
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            yield return new SignatureFinding
            {
                Signature = SharedNode,
                Headline = $"{SharedNode.Title}: {group.Key}",
                Hosts = group.Select(x => x.Report.Host).ToList(),
                Lines = group.Select(x => $"{x.Report.Host} — {Verdict(x.Report)}").ToList(),
            };
        }
    }

    private static IEnumerable<SignatureFinding> PartlySilentNames(List<TargetReport> reports)
    {
        var partly = reports.Where(r => r.Tcp.Silent.Count > 0).ToList();

        if (partly.Count == 0)
            yield break;

        yield return new SignatureFinding
        {
            Signature = PartlySilent,
            Headline = PartlySilent.Title,
            Hosts = partly.Select(r => r.Host).ToList(),
            Lines = partly.Select(r => $"{r.Host} — молчали {string.Join(", ", r.Tcp.Silent)}"
                + (r.Answered is { } a ? $", ответил {a}" : string.Empty)).ToList(),
            Calm = true,
        };
    }

    private static string Verdict(TargetReport report) =>
        report.Spoof.Verdict == DnsVerdict.ForeignCertificate && report.Spoof.Detail.Length > 0
            ? report.Spoof.Detail
            : report.Describe();
}
