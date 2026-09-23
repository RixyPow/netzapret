using System.Net;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>Чем имя выведено из-под десинка.</summary>
/// <remarks>
/// Причины две, и различать их приходится ради отчёта: лечатся они по-разному.
/// Пин снимается в разделе «Файл hosts», «напрямую» — переключателем
/// в маршрутах, и совет «уберите исключение» без указания, какое именно,
/// отправляет искать не туда.
/// </remarks>
public enum DesyncBypass
{
    /// <summary>Ничем: десинк к имени применяется.</summary>
    None,

    /// <summary>Прибито в hosts своим адресом.</summary>
    Pin,

    /// <summary>Поставлено на «напрямую».</summary>
    Direct,

    /// <summary>
    /// Поставлено «через VPN», а туннель не поднят.
    /// </summary>
    /// <remarks>
    /// Таблица владельца 23.09: при одном десинке «VPN идёт напрямую».
    /// Везти такое имя некуда, и десинк к нему не применяется — ровно как
    /// к «напрямую». Отдельной причиной, а не <see cref="Direct"/>: лечится
    /// это не переключателем в маршрутах, а выключателем туннеля.
    /// </remarks>
    VpnWithoutTunnel,

    /// <summary>
    /// Поставлено «через VPN», и туннель поднят.
    /// </summary>
    /// <remarks>
    /// Считалось, что такое имя десинку не видно: трафик уходит в туннель.
    /// Замер 23.09 опроверг. Фильтр перехвата winws2 (<c>--wf-tcp-out</c>)
    /// не ограничен интерфейсом и видит пакеты, которые приложение шлёт
    /// в TUN, — и секция пресета «Выбрано вручную: instagram» с поддельными
    /// пакетами (<c>tcp_md5</c>, <c>tcp_ts=-1000</c>) портила рукопожатие
    /// уже внутри туннеля. Настоящий сервер такие подделки отбрасывает,
    /// стек TUN в sing-box — нет. Instagram не открывался через туннель,
    /// хотя все пять выходов доставали его сами за 150–470 мс, а LinkedIn
    /// и WhatsApp через тот же туннель открывались.
    /// </remarks>
    Tunnel,
}

/// <summary>
/// Читает системный файл hosts.
/// </summary>
/// <remarks>
/// <para>
/// Нужен потому, что hosts бьёт любой резолв, включая наш. Домен, прибитый
/// там к адресу, никогда не дойдёт до sing-box и не получит fakeip — Windows
/// ответит из файла. Для правила <c>mode: proxy</c> это значит, что оно молча
/// не работает: трафик уходит напрямую на прибитый адрес, под блокировку.
/// </para>
/// <para>
/// Ровно так и вышло с Canva, RuTracker и LinkedIn: записи оставил редактор
/// hosts из Zapret GUI, и со стороны это выглядело как неисправный VPN.
/// Bandcamp в файле не значился и работал — по этому расхождению причину
/// и нашли.
/// </para>
/// <para>
/// Только чтение. Файл ведёт другая программа, и вычищать её записи мы
/// не вправе: они могут быть нужны для десинка.
/// </para>
/// </remarks>
public static class HostsFile
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers",
        "etc",
        "hosts");

    /// <summary>
    /// Возвращает соответствие «имя — прибитые адреса».
    /// </summary>
    public static IReadOnlyDictionary<string, List<IPAddress>> Read(string? path = null)
    {
        var result = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var target = path ?? DefaultPath;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(target);
        }
        catch (Exception)
        {
            // Недоступен или отсутствует — считаем, что записей нет.
            // Ради чтения hosts запуск останавливать незачем.
            return result;
        }

        foreach (var raw in lines)
        {
            var line = raw;
            int comment = line.IndexOf('#');

            if (comment >= 0)
                line = line[..comment];

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // Минимум адрес и одно имя.
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address))
                continue;

            foreach (var name in parts.Skip(1))
            {
                if (!result.TryGetValue(name, out var list))
                    result[name] = list = [];

                if (!list.Contains(address))
                    list.Add(address);
            }
        }

        return result;
    }

    /// <summary>Заглушка ли это: адрес, которым имя закрывают, а не уводят.</summary>
    public static bool IsBlocking(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any);

    public static string ToCidr(IPAddress address) =>
        $"{address}/{(address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32)}";

    /// <summary>
    /// Находит адреса, прибитые в hosts к доменам с правилом <c>mode: proxy</c>,
    /// чтобы завести их в туннель принудительно.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Иначе такие домены оставались бы вне туннеля независимо от правил:
    /// fakeip им не выдаётся, а перехват идёт по адресу назначения. Заводим
    /// прибитый адрес — трафик доходит до sing-box, тот по SNI опознаёт домен
    /// и применяет к нему обычное правило.
    /// </para>
    /// <para>
    /// Списочные правила учитываются наравне с доменными. Пока учитывались
    /// только доменные, это молча не работало для всего, что назначено через
    /// раздел сервисов, — а он пишет именно <c>hostlist</c>. Так и вышло
    /// с ChatGPT: правило говорило «через VPN», в hosts все его имена были
    /// прибиты к одному давно умолкшему адресу, и трафик уходил туда мимо
    /// туннеля. Со стороны — блокировка, которую ничем не пробить.
    /// </para>
    /// </remarks>
    /// <param name="notes">Что нашлось — для показа пользователю.</param>
    /// <param name="hostsPath">Путь к файлу; <c>null</c> — системный.</param>
    public static IReadOnlyList<string> CollectPinnedProxyAddresses(
        Core.Rules.RuleSet ruleSet,
        out IReadOnlyList<string> notes,
        string? hostsPath = null)
    {
        var found = new List<string>();
        var messages = new List<string>();
        notes = messages;

        var hosts = Read(hostsPath);

        if (hosts.Count == 0)
            return found;

        // Имена собираются в набор, а не печатаются на месте. Одно имя обычно
        // покрыто несколькими правилами сразу — своим и поставляемым, — и
        // построчный вывод повторял его столько раз, сколько правил совпало.
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // Правила разбираются по порядку, и решает первое совпавшее — так же,
        // как их вычисляет движок. Прежде здесь перебирались все правила
        // с mode: proxy подряд, без вопроса, какое из них выигрывает.
        //
        // Стоило это работающего сервиса. Поставляемое «*.chatgpt.com → VPN»
        // перекрыто пользовательским «напрямую», то есть не действует, — а
        // сборщик всё равно находил по нему прибитый адрес и заводил его
        // в туннель. Пин при этом был жив: с остановленной программой сайт
        // открывался, с запущенной умирал сразу.
        var order = ruleSet.Rules.Select(r => (r.Mode, Domains: DomainsOf(r).ToList())).ToList();

        foreach (var (name, addresses) in hosts)
        {
            if (addresses.Count == 0)
                continue;

            var mode = FirstMatch(order, name);

            if (mode != Core.Rules.RoutingMode.Proxy)
                continue;

            // Петлевые и нулевые адреса ставят, чтобы заблокировать имя,
            // а не увести его. В туннель их заводить бессмысленно, а петлю
            // и вредно: маршрут 127.0.0.1 через TUN понёс бы к sing-box
            // локальный трафик машины. Этот отсев жил в FindPinned, которую
            // звала только консоль; сборщик окна его не делал (найдено 23.09).
            var real = addresses.Where(a => !IsBlocking(a)).ToList();

            if (real.Count == 0)
                continue;

            foreach (var address in real)
            {
                var cidr = ToCidr(address);

                if (!found.Contains(cidr))
                    found.Add(cidr);
            }

            names.Add(name);
        }

        if (names.Count == 0)
            return found;

        // Одной строкой, и это осознанный выбор. Прежде здесь печаталось
        // по строке на имя — шестнадцать подряд при каждом запуске, все
        // одинаковой формы и с повторами. Такой список не читают: он выглядит
        // чередой предупреждений о неисправности, тогда как описывает
        // проделанную работу, и приучает пропускать всё, что начинается
        // со слова «внимание».
        messages.Add(
            $"прибитых имён с правилом «через VPN»: {names.Count} " +
            $"({Sample(names)}) — их адреса заведены в туннель, " +
            "иначе правила для них не сработали бы");

        return found;
    }

    /// <summary>
    /// Имена, которые десинку трогать нельзя.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Источника два, и оба означают одно: «этому имени вмешательство
    /// не нужно». Поэтому и список один.
    /// </para>
    /// <para>
    /// Первый — пин в файле hosts. Это выбранный руками адрес вместо того,
    /// что даёт резолвер, и выбран он потому, что работает. Десинк опознаёт
    /// имя в ClientHello, про подмену адреса не знает и применяет рецепт,
    /// выверенный на настоящей сети доставки, — к постороннему узлу, которому
    /// этот рецепт не нужен и вреден. Соединение рвётся на рукопожатии.
    /// </para>
    /// <para>
    /// Второй — правило «напрямую». Прежде оно означало только «мимо туннеля»,
    /// и для winws2 ничем не отличалось от «десинка»: имя всё равно попадало
    /// в секцию пресета и получало рецепт. Снаружи это выглядело так, будто
    /// переключатель не работает, и один такой случай стоил вечера разбора —
    /// у части Discord стояло «напрямую», а к discord.media применялся
    /// <c>send + syndata</c> из пресета.
    /// </para>
    /// <para>
    /// Третий — «через VPN». Долго считалось, что такие имена сюда не идут:
    /// трафик уходит в туннель, и десинк его не видит. Замер 23.09 это
    /// опроверг — winws2 видит пакеты, которые приложение шлёт в TUN,
    /// и рецепт на поддельных пакетах портил рукопожатие внутри туннеля.
    /// См. <see cref="DesyncBypass.Tunnel"/>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> CollectDesyncExclusions(
        Core.Rules.RuleSet ruleSet,
        string? hostsPath = null,
        bool tunnelUp = true) =>
        DescribeDesyncExclusions(ruleSet, hostsPath, tunnelUp).Select(each => each.Name).ToList();

    /// <summary>
    /// То же самое, но с причиной у каждого имени.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Движку причина не нужна — ему уходит плоский список, — а отчёту нужна.
    /// Проверка блокировок про исключение молчала вовсе: имя, выведенное
    /// из-под десинка, проваливалось и получало вердикт «DPI по TLS» с советом
    /// «десинк», которого к нему по нашему же решению не применяют. Со стороны
    /// это неотличимо от неудачного рецепта, и вечер уходил на правку секции,
    /// до которой дело не доходит.
    /// </para>
    /// <para>
    /// Порядок перебора тот же, что и был, и он значим: пин читается первым,
    /// поэтому имя, и прибитое в hosts, и поставленное на «напрямую», числится
    /// за пином. Так честнее — пин бьёт резолв независимо от режима.
    /// </para>
    /// <para>
    /// <paramref name="tunnelUp"/> — поднят ли туннель вместе с десинком.
    /// «Через VPN» выводится из-под десинка в обоих случаях, меняется лишь
    /// причина: без туннеля имя идёт напрямую (таблица владельца 23.09),
    /// с туннелем — в туннель, где десинк его только портит.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Name, DesyncBypass Why)> DescribeDesyncExclusions(
        Core.Rules.RuleSet ruleSet,
        string? hostsPath = null,
        bool tunnelUp = true)
    {
        var found = new List<(string Name, DesyncBypass Why)>();
        var order = ruleSet.Rules.Select(r => (r.Mode, Domains: DomainsOf(r).ToList())).ToList();

        void Add(string name, DesyncBypass why)
        {
            if (!found.Any(each => string.Equals(each.Name, name, StringComparison.OrdinalIgnoreCase)))
                found.Add((name, why));
        }

        foreach (var (name, addresses) in Read(hostsPath))
        {
            if (addresses.Count == 0)
                continue;

            // Все прибитые, и под VPN тоже: адрес такого пина заведён
            // в туннель маршрутом, а winws2 видит и то, что идёт в TUN.
            Add(name, DesyncBypass.Pin);
        }

        // Имена правил «напрямую» — но только те, которым это правило
        // и достаётся. Порядок здесь решает всё: правило может быть
        // перекрыто более ранним, и тогда имя живёт по чужому режиму.
        // У Discord так и вышло — «обновления» стоят на «напрямую», а до них
        // имя забирает список сайта, стоящий выше и покрывающий ту же зону.
        // Записав такое имя в исключения, мы отменили бы десинк там, где
        // человек его не отменял.
        foreach (var (mode, domains) in order)
        {
            if (mode != Core.Rules.RoutingMode.Direct)
                continue;

            foreach (var domain in domains)
            {
                if (FirstMatch(order, domain) == Core.Rules.RoutingMode.Direct)
                    Add(domain, DesyncBypass.Direct);
            }
        }


        // Тем же порядком и с той же оговоркой: имя достаётся тому правилу,
        // до которого очередь доходит раньше, и «через VPN», перекрытое
        // десинком выше, остаётся десинку.
        foreach (var (mode, domains) in order)
        {
            if (mode != Core.Rules.RoutingMode.Proxy)
                continue;

            foreach (var domain in domains)
            {
                if (FirstMatch(order, domain) == Core.Rules.RoutingMode.Proxy)
                    Add(domain, tunnelUp ? DesyncBypass.Tunnel : DesyncBypass.VpnWithoutTunnel);
            }
        }

        return found;
    }

    /// <summary>
    /// Чем выведено из-под десинка это имя; <see cref="DesyncBypass.None"/> — ничем.
    /// </summary>
    /// <remarks>
    /// Сравнение по зоне, а не дословное. Список уезжает движку файлом,
    /// а файловый список winws2 раскрывает до поддоменов сам — в его справке
    /// у <c>--hostlist=</c> так и написано, «subdomains auto apply». Дословное
    /// сравнение сказало бы про <c>api.openai.com</c>, что десинк к нему
    /// применяется, тогда как движок не трогает и его — из-за записи
    /// <c>openai.com</c> строкой выше.
    /// </remarks>
    public static DesyncBypass BypassFor(
        IReadOnlyList<(string Name, DesyncBypass Why)> exclusions,
        string host)
    {
        foreach (var (name, why) in exclusions)
        {
            var zone = name.TrimStart('*', '.');

            if (string.Equals(zone, host, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            {
                return why;
            }
        }

        return DesyncBypass.None;
    }

    /// <summary>Пометка для отчёта; пусто — имя десинку доступно.</summary>
    /// <remarks>
    /// Слова одни на консоль и на окно. Разойдись они — два вида одного и того
    /// же замера объясняли бы его по-разному, и сверять их пришлось бы вручную.
    /// </remarks>
    public static string DescribeBypass(DesyncBypass bypass) => bypass switch
    {
        DesyncBypass.Pin => "мимо десинка: пин в hosts",
        DesyncBypass.Direct => "мимо десинка: «напрямую»",
        DesyncBypass.VpnWithoutTunnel => "мимо десинка: «через VPN», а туннель выключен",
        DesyncBypass.Tunnel => "мимо десинка: идёт в туннель",
        _ => string.Empty,
    };

    /// <summary>
    /// Режим первого правила, покрывающего имя; <c>null</c> — ни одного.
    /// </summary>
    /// <remarks>
    /// Повторяет порядок вычисления движка: правила проверяются сверху вниз,
    /// решает первое совпавшее, остальные не смотрятся вовсе. Сравнение
    /// по зоне — запись <c>openai.com</c> покрывает <c>api.openai.com</c>,
    /// как и в самих списках.
    /// </remarks>
    private static Core.Rules.RoutingMode? FirstMatch(
        IReadOnlyList<(Core.Rules.RoutingMode Mode, List<string> Domains)> order,
        string name)
    {
        foreach (var (mode, domains) in order)
        {
            foreach (var domain in domains)
            {
                var zone = domain.TrimStart('*', '.');

                if (string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
                {
                    return mode;
                }
            }
        }

        return null;
    }

    /// <summary>Несколько имён для примера и хвост числом.</summary>
    private static string Sample(IReadOnlyCollection<string> names)
    {
        const int show = 3;

        return names.Count <= show
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(show)) + $" и ещё {names.Count - show}";
    }

    /// <summary>
    /// Имена, на которые правило распространяется.
    /// </summary>
    /// <remarks>
    /// Списочное правило — это те же домены, только перечисленные в файле;
    /// список к этому времени уже загружен разворачивателем правил. Имена
    /// со звёздочкой внутри пропускаются: сопоставлять их с hosts, где
    /// подстановок не бывает, не с чем.
    /// </remarks>
    private static IEnumerable<string> DomainsOf(Core.Rules.RoutingRule rule)
    {
        IEnumerable<string> raw = rule.Match switch
        {
            Core.Rules.MatchKind.Domain => [rule.Value],
            Core.Rules.MatchKind.HostList => rule.HostListDomains,
            _ => [],
        };

        foreach (var value in raw)
        {
            var domain = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;

            if (!domain.Contains('*'))
                yield return domain;
        }
    }
}
