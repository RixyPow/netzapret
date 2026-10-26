using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Subscriptions;

namespace NetZapret.Proxy;

/// <summary>Для какой системы собирается конфиг движка.</summary>
/// <remarks>
/// Движок один — sing-box extended, — но живёт по-разному: на Windows это
/// sing-box.exe рядом с winws2, на телефоне — libbox внутри VPN-службы
/// приложения. От этого зависят входы, Clash API, имя TUN и путь «десинка»
/// (<see cref="SingBoxOptions.Platform"/>).
/// </remarks>
public enum SingBoxPlatform
{
    Windows,
    Android,
}

/// <summary>
/// ByeDPI на телефоне без root: SOCKS5 на петле с именем и паролем на сессию.
/// </summary>
/// <remarks>
/// Пароль — секрет того же рода, что ключи серверов (CLAUDE.md, «Секреты»):
/// любое приложение на телефоне, знающее порт и пароль, ходит через ByeDPI
/// от нашего имени мимо VPN. Поэтому <see cref="ToString"/> его не печатает.
/// </remarks>
public sealed record DesyncSocks(int Port, string Username, string Password)
{
    public override string ToString() => $"ByeDPI на 127.0.0.1:{Port}";
}

/// <summary>Чем телефон отличается от Windows для сборки конфига.</summary>
public sealed record AndroidEngine
{
    /// <summary>Файл пинов в папке приложения, полным путём (<c>&lt;files&gt;/config/hosts</c>).</summary>
    public required string HostsFilePath { get; init; }

    /// <summary>
    /// Есть root и поднят nfqws2: «десинк» тогда — <c>direct</c>, пакеты ловит
    /// nfqws2 в iptables, как winws2 на Windows.
    /// </summary>
    public bool Root { get; init; }

    /// <summary>
    /// Приложения мимо VPN. Своё приложение хозяин кладёт сюда сам: через
    /// него ходят ByeDPI и пробы «напрямую», и в туннель им нельзя.
    /// </summary>
    public IReadOnlyList<string> ExcludePackages { get; init; } = [];

    /// <summary>Только эти приложения через VPN; вместе с <see cref="ExcludePackages"/> нельзя.</summary>
    public IReadOnlyList<string> IncludePackages { get; init; } = [];

    /// <summary>Нарезка приветствия, когда нет ни root, ни ByeDPI (<see cref="Socks"/> — <c>null</c>).</summary>
    public TlsFragment DesyncFragment { get; init; } = TlsFragment.Records;

    /// <summary>Пауза <c>tls_fragment</c> без замера ACK; <c>null</c> — 500 мс движка.</summary>
    public string? DesyncFragmentFallbackDelay { get; init; }

    /// <summary>QUIC к именам «десинка» — отказом, чтобы браузер ушёл на TCP. Не замерено.</summary>
    public bool DesyncRejectQuic { get; init; } = true;

    /// <summary>
    /// Невыведенное при «по умолчанию — десинк» — тоже нарезкой (без ByeDPI).
    /// Выключено: нарезка не знает списков пресета и задела бы всё подряд.
    /// </summary>
    public bool DesyncFragmentDefault { get; init; }

    /// <summary>ByeDPI для маршрута «десинк» без root; <c>null</c> — нарезка движком.</summary>
    public DesyncSocks? Socks { get; init; }

    /// <summary>
    /// Невыведенное при «по умолчанию — десинк» — тоже в ByeDPI. Включено:
    /// ByeDPI сам решает по спискам пресета, как winws2 на Windows, — без
    /// этого YouTube и Discord до него не доходили бы (SingBoxOptions.DesyncDefault).
    /// </summary>
    public bool DesyncSocksDefault { get; init; } = true;

    /// <summary>Имена секций <c>pass</c> пресета (<c>ByeDpiPlan.PassHosts</c>): напрямую, мимо ByeDPI.</summary>
    public IReadOnlyList<string> DesyncPassHosts { get; init; } = [];
}

/// <summary>
/// Что знает хозяин и не может узнать библиотека: окно на Windows, VPN-служба на телефоне.
/// </summary>
/// <remarks>
/// Делегатами, а не ссылкой на проекты: <c>NetZapret.Proxy</c> не ссылается на
/// <c>NetZapret.Zapret</c> (разворот списков), а реестр и адаптеры Windows
/// на телефоне не читаются. Так сборка конфига — одна на обе системы, и
/// правка компилятора доходит до телефона пересборкой (docs/android.md).
/// </remarks>
public sealed record EngineHost
{
    public SingBoxPlatform Platform { get; init; } = SingBoxPlatform.Windows;

    /// <summary>Есть ли IPv6 для TUN. Windows: <c>!WindowsIpv6.Disabled()</c>; телефон: <c>true</c>.</summary>
    public required Func<bool> Ipv6 { get; init; }

    /// <summary>Резолверы системы префиксами — для выборочного перехвата. Телефон: пусто.</summary>
    public required Func<IReadOnlyList<string>> SystemResolvers { get; init; }

    /// <summary>Правила так, как их увидят движки (<c>RuleSetExpander.LoadFor</c>), и корень Zapret.</summary>
    public required Func<AppSettings, (RuleSet RuleSet, string? ZapretRoot)> LoadRules { get; init; }

    /// <summary>Разворот записей списков адресов (<c>AddressListReader.Expand</c>).</summary>
    public required Func<IReadOnlyList<string>, string?, IReadOnlyList<string>> ExpandAddressLists { get; init; }

    /// <summary>Подписки в работе — из книги (<see cref="SubscriptionBook"/>).</summary>
    public Func<SubscriptionSources> Subscriptions { get; init; } = () => SubscriptionSources.Read();

    public Func<AppSettings, RouterDnsConnection.Selection> RouterDns { get; init; } = s => RouterDnsConnection.Resolve(s);

    /// <summary>Строка в журнал: (кто пишет, что). Ссылок и ключей сюда не передаётся.</summary>
    public Action<string, string> Journal { get; init; } = (_, _) => { };

    /// <summary>Файл пинов; <c>null</c> — <see cref="HostsFile.DefaultPath"/> или файл телефона.</summary>
    public string? HostsPath { get; init; }

    /// <summary>Обязателен при <see cref="Platform"/> = Android.</summary>
    public AndroidEngine? Android { get; init; }
}

/// <summary>Подписки для сборки: пул, ключи и все ссылки книги (для панелей мимо туннеля).</summary>
/// <param name="Pool">Подписки в работе. Ссылки — пароли: не печатаются.</param>
/// <param name="PoolKeys">Отдельные ключи в работе.</param>
/// <param name="PanelUrls">Ссылки всех подписок книги, и не в работе тоже.</param>
public sealed record SubscriptionSources(
    IReadOnlyList<PoolSource> Pool,
    IReadOnlyList<string> PoolKeys,
    IReadOnlyList<string?> PanelUrls)
{
    public static SubscriptionSources Empty { get; } = new([], [], []);

    /// <summary>Из книги подписок (<c>config/subscriptions.json</c>), прочитанной заново или данной.</summary>
    public static SubscriptionSources Read(SubscriptionBook? book = null)
    {
        var source = book ?? SubscriptionBook.Load();

        return new(
            source.Pool.Select(e => new PoolSource(e.Name, e.Url)).ToList(),
            source.PoolKeys,
            source.Entries.Select(e => (string?)e.Url).ToList());
    }

    // Ссылки и ключи — пароли: запись не печатает их ни при каком ToString.
    public override string ToString() => $"подписок в работе: {Pool.Count}, ключей: {PoolKeys.Count}";
}

/// <summary>Чем закончилась сборка конфига.</summary>
/// <param name="Compiled">Собранное; <c>null</c> — не собралось.</param>
/// <param name="Pool">Что дали подписки — окну: перенос ссылки (<c>new-url</c>) и напоминание о сроке.</param>
/// <param name="Deferred">Подписки из запаса, которые спросить после запуска.</param>
public sealed record EngineBuild(
    bool Ok,
    string Message,
    string? Warning,
    CompilationResult? Compiled,
    PoolResult? Pool,
    IReadOnlyList<PoolPart> Deferred);

/// <summary>Что поднимать.</summary>
/// <param name="Tunnel">Туннель с выходами.</param>
/// <param name="DnsOnly">Движок только ради DNS при одном десинке (<see cref="AppSettings.NeedsDnsEngine"/>).</param>
/// <param name="FragmentOnly">
/// Телефон без root: туннеля нет, а десинк есть — движок нужен, чтобы вести
/// «десинк» в ByeDPI или резать приветствие. Собирается <see cref="EngineConfigBuilder.BuildTunnelAsync"/> без выходов.
/// </param>
public sealed record EnginePlan(bool Tunnel, bool DnsOnly, bool FragmentOnly);

/// <summary>Собранное заранее для <see cref="EngineConfigBuilder.TunnelOptions"/>.</summary>
/// <param name="PanelUrls">Ссылки подписок книги — для имён панелей мимо туннеля.</param>
public sealed record TunnelInputs(
    RouterDnsConnection.Selection RouterDns,
    bool Narrow,
    bool ForcedByProgram,
    IReadOnlyList<string> Capture,
    IReadOnlyList<string> KeptOut,
    PinnedProxy Pinned,
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlyCollection<string> Dead,
    ServerHealthCache Health,
    EngineKeys? Keys,
    IReadOnlyList<string?> PanelUrls);

/// <summary>
/// Сборка конфига движка из настроек, правил и подписок — одна для окна и телефона.
/// </summary>
/// <remarks>
/// <para>
/// До 27.10 жила в окне (<c>TunnelConfig</c>), и телефону пришлось бы
/// повторять её вторым кодом — а он расходился бы с первым с каждой правкой.
/// Теперь окно и телефон зовут одно и то же; разница — в <see cref="EngineHost"/>.
/// </para>
/// <para>
/// Побочные действия окна — перенос ссылки подписки в книге, напоминание
/// о сроке, перепроверка отложенных панелей — остались окну: сборка
/// возвращает для них <see cref="EngineBuild.Pool"/> и <see cref="EngineBuild.Deferred"/>.
/// </para>
/// </remarks>
public static class EngineConfigBuilder
{
    /// <summary>Что поднимать при этих настройках.</summary>
    public static EnginePlan Plan(AppSettings settings, EngineHost host)
    {
        bool tunnel = settings.NeedsProxy;

        if (host.Platform != SingBoxPlatform.Android)
            return new EnginePlan(tunnel, settings.NeedsDnsEngine, false);

        // Без root десинк живёт в самом движке (ByeDPI исходящим или нарезка),
        // так что при одном десинке движок нужен целиком, а не ради DNS:
        // DNS он тогда тоже делает. С root десинк — nfqws2 рядом, как на Windows.
        bool desyncInside = settings.NeedsDesync && AndroidOf(host) is { Root: false };
        bool alone = !tunnel && desyncInside;

        return new EnginePlan(tunnel, !tunnel && !alone && settings.NeedsDnsEngine, alone);
    }

    /// <summary>
    /// Собирает и пишет конфиг туннеля (<see cref="AppSettings.ProxyConfigPath"/>).
    /// </summary>
    /// <remarks>
    /// Тело прежнего <c>TunnelConfig.BuildAsync</c> окна без его побочных
    /// действий. На телефоне без root и без туннеля (<see cref="EnginePlan.FragmentOnly"/>)
    /// собирает тот же конфиг без выходов: «через VPN» тогда — напрямую, как
    /// при одном десинке на Windows.
    /// </remarks>
    public static async Task<EngineBuild> BuildTunnelAsync(
        AppSettings settings, EngineHost host, CancellationToken cancellationToken)
    {
        var android = AndroidOf(host);
        bool alone = Plan(settings, host) is { Tunnel: false, FragmentOnly: true };

        // Выхода нет вовсе — ни подписки, ни WARP. Дальше идти незачем:
        // конфиг вышел бы с пустой группой выходов, и движок держал бы TUN,
        // не умея никуда доставить.
        //
        // Смотрим на оба источника, а не на одну ссылку: WARP заведён ровно
        // затем, чтобы работать без подписки, и прежде он в одиночку
        // не поднимался — отказ приходил раньше, чем кто-либо смотрел
        // на выключатель.
        if (!settings.HasTunnelExit && !alone)
        {
            return Failed(
                "Ни подписки, ни WARP — заворачивать трафик некуда. "
                + "Добавьте ссылку в разделе «VPN» либо включите там же бесплатный WARP.");
        }

        PoolResult? pool = null;

        try
        {
            var requestedDns = settings;
            var routerDns = host.RouterDns(settings);
            settings = routerDns.Settings;
            if (routerDns.Warning is not null) host.Journal("DNS", routerDns.Warning);
            var (ruleSet, zapretRoot) = host.LoadRules(settings);

            // Десинк без туннеля на телефоне правила «Маршрутов» исполняет сам:
            // как в выборочном режиме без выхода — «через VPN» уходит напрямую
            // (ResolveOutbound без серверов), «десинк» — в ByeDPI или с нарезкой.
            // В режиме «только десинк» движок их не писал бы вовсе.
            if (alone)
                ruleSet = ruleSet with { Operating = OperatingMode.Selective };

            var sources = host.Subscriptions();

            // WARP и подписки — одно из двух (владелец, 01.10; Warp.TunnelExits).
            // При включённом WARP подписки на паузе, и ходить за ними незачем:
            // запуск не ждёт панелей, которые всё равно не понадобятся.
            IReadOnlyList<ProxyServer> fromSubscription = [];
            int sourceCount = 0, keyCount = 0, reserved = 0;
            IReadOnlyList<PoolPart> deferred = [];

            if (!settings.WarpEnabled && !alone)
            {
                // Пул (0.9.0): серверы всех подписок в работе, а не одной
                // действующей. Указатель из настроек — на случай, когда список
                // подписок пуст, а ссылку поставила консоль.
                var poolSources = sources.Pool.ToList();

                if (poolSources.Count == 0 && !string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
                    poolSources.Add(new PoolSource("Основная", settings.SubscriptionUrl));

                // Отдельные ключи — последним источником пула (0.9.0). Панели
                // с запасом моложе суток запуск не ждёт — их спросят после
                // (SubscriptionPool.StartFromReserveFor, SubscriptionRecheck).
                var built = await SubscriptionPool.BuildAsync(
                    poolSources, cancellationToken, keys: sources.PoolKeys, deferPanels: true);

                deferred = built.Parts.Where(p => p.Deferred).ToList();
                fromSubscription = built.Servers;
                sourceCount = poolSources.Count;
                keyCount = sources.PoolKeys.Count;
                reserved = built.Parts.Count(p => p.FromReserve);

                // Подписки были, а серверов нет ни от одной, и запаса тоже:
                // собирать не из чего, и сказать надо, почему. Имена подписок,
                // не ссылки: ссылка — пароль.
                if ((poolSources.Count > 0 || sources.PoolKeys.Count > 0) && fromSubscription.Count == 0)
                {
                    return Failed("Конфиг не собрался: ни подписки, ни ключи не дали серверов: "
                        + string.Join("; ", built.Parts.Select(p => $"«{p.Source.Name}» — {p.Error ?? "пусто"}")) + ".");
                }

                pool = built;

                // Кто пришёл из запаса — в журнал: без этого «сервер из вчерашнего
                // списка» было бы нечем объяснить.
                foreach (var part in built.Parts.Where(p => p.FromReserve || p.Error is not null))
                {
                    host.Journal("подписка", part.FromReserve
                        ? $"«{part.Source.Name}» не ответила{(PhysicalNetwork.Up() ? "" : $" ({PhysicalNetwork.Offline})")} — "
                            + $"серверы из запаса от {part.ReserveAt:dd.MM HH:mm}"
                        : $"«{part.Source.Name}» не прочиталась и запаса нет: {part.Error}");
                }

                foreach (var part in deferred)
                {
                    host.Journal("подписка",
                        $"«{part.Source.Name}»: запуск из запаса от {part.ReserveAt:dd.MM HH:mm}, панель спрошу после запуска");
                }
            }

            var servers = alone ? [] : Warp.TunnelExits(settings, fromSubscription);

            var capture = host.ExpandAddressLists(ruleSet.CaptureEntries, zapretRoot);

            // hosts бьёт любой резолв, включая наш: прибитый там домен
            // не получит fakeip и уйдёт мимо туннеля, сколько бы правил
            // на него ни стояло. На телефоне «hosts» — свой файл приложения.
            var pinned = HostsFile.CollectPinnedProxy(ruleSet, out _, host.HostsPath ?? android?.HostsFilePath);

            var addresses = AddressOverrides.Merge(new Dictionary<string, string>(), AddressOverrides.Load());

            // Кто из серверов заведомо мёртв — по нашим же замерам. Сборка
            // в сеть не ходит и судить о живости не может, поэтому список
            // приносим ей мы.
            var health = ServerHealthCache.Load();
            var dead = health.Dead(new SingBoxOptions().DeadAfterFailures);

            // «В пуле только рабочие» (владелец, 28.09) — буквально: мёртвый
            // по замерам сервер в конфиг не идёт вовсе, а не только выводится
            // из автоподбора. С пулом из трёх подписок в конфиге стало 118
            // выходов, и sing-box поднимался 7 секунд против прежних двух;
            // каждый мёртвый выход — лишняя работа движку ни за что.
            // Закреплённый человеком остаётся — его выбрали руками. И не до
            // пустоты: мёртвыми бывают все сразу — по устаревшим замерам, —
            // и тогда пусть движок перебирает всех. В списке вкладки мёртвые
            // видны по-прежнему; удачный замер вернёт сервер со следующим
            // запуском.
            //
            // Так же закреплённые в пулах и пул, мёртвый целиком (идеи №6, 19.10):
            // выброшенный пул без серверов исчез бы из конфига, и его маршруты
            // молча ушли бы в пул по умолчанию — даже когда человек велел ждать.
            var keep = ServerPools.KeptDespiteDeath(settings.EnginePools, servers.Select(s => s.Tag), dead);

            var alive = servers
                .Where(s => !dead.Contains(s.Tag) || s.Tag == Warp.PreferredExit(settings) || s.IsSelfRegistering
                    || keep.Contains(s.Tag))
                .ToList();

            int dropped = servers.Count - alive.Count;

            if (alive.Any(s => !s.IsSelfRegistering))
                servers = alive;
            else
                dropped = 0;

            // Охват TUN следует за десинком, а не за настройкой (21.09):
            // «только прокси» заведено ради сосуществования с winws2 — TUN
            // забирает один диапазон fakeip, и WinDivert видит исходные потоки
            // приложений. Без десинка защищать нечего, а «всё через туннель»
            // такая настройка ломала начисто: ютуб со своим настоящим адресом
            // в туннель не попадал. С 23.09 — по TunnelTakesAll; с 29.09 — и не
            // при программе, которую правила ведут в туннель (№10); с 01.10 —
            // только без десинка (ProgramCapture.ForcesFullCapture, владелец:
            // при десинке весь трафик через движок ломал голос Discord).
            bool byProgram = ProgramCapture.ForcesFullCapture(ruleSet, settings.NeedsDesync);
            bool narrow = !settings.Engines.TunnelTakesAll && settings.ProxyOnly && !byProgram;

            // На телефоне без root выборочного перехвата нет: десинк живёт
            // в движке, и до ByeDPI или нарезки доходит только то, что вошло
            // в TUN. Решение «полного перехвата в Гибриде не будет» стоит на
            // устройстве Windows (winws2 видит только то, что мимо TUN) и сюда
            // не переносится (docs/android.md). С root nfqws2 рядом — как Windows.
            if (android is { Root: false })
                narrow = false;

            // Полный перехват только ради программы — игры с маршрутом мимо VPN
            // выводятся из туннеля и идут так же, как без этого правила
            // (ProgramCapture, 30.09). Выбранный человеком режим «всё через
            // туннель» не трогаем: там туннель забирает всё по его решению.
            bool forcedByProgram = !settings.Engines.TunnelTakesAll && settings.ProxyOnly && byProgram;

            var keptOut = forcedByProgram
                ? host.ExpandAddressLists(ProgramCapture.KeepOut(ruleSet), zapretRoot)
                : [];

            var options = TunnelOptions(settings, host, new TunnelInputs(
                routerDns, narrow, forcedByProgram, capture, keptOut, pinned, addresses, dead, health,

                // Новые пароли служебных входов на каждую сборку: окно, nz
                // и сторож читают их из этого же конфига (EngineKeys.Current).
                // На телефоне служебных входов нет — и паролей тоже.
                android is null ? EngineKeys.Generate() : null,
                sources.PanelUrls));

            var result = new SingBoxConfigCompiler().Compile(ruleSet, servers, options);

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);
            RouterDnsBuildState.Save(settings.ProxyConfigPath, requestedDns, routerDns.Warning);

            var note = alone
                ? "Конфиг собран: туннеля нет, «десинк» ведёт движок — "
                    + (options.DesyncSocks is not null ? "в ByeDPI" : "нарезкой приветствия")
                : settings.WarpEnabled
                    ? "Конфиг собран: туннель через WARP, подписки на паузе"
                    : $"Конфиг собран: {result.UsedServers.Count} серверов"
                        + (sourceCount > 1 ? $" из {sourceCount} подписок" : string.Empty)
                        + (keyCount > 0 ? $" и {keyCount} ключей" : string.Empty);

            if (reserved > 0)
                note += $", {reserved} из запаса — " + (PhysicalNetwork.Up() ? "панель не ответила" : "нет сети");

            // Про мёртвых говорим вслух. Молча выведенный из автоподбора
            // сервер — это сервер, который человек считает рабочим, а он
            // не участвует в выборе, и почему — не видно нигде.
            if (dead.Count > 0 && !alone)
                note += dropped > 0
                    ? $", {dropped} не отвечали и в конфиг не взяты"
                    : $", {dead.Count} не отвечали и выведены из автоподбора";

            if (result.SkippedServers.Count > 0)
                note += $", {result.SkippedServers.Count} пропущено";

            return new EngineBuild(true, note + ".", routerDns.Warning, result, pool, deferred);
        }
        catch (Exception ex)
        {
            // Пул отдаётся и здесь: прежде окно переносило ссылку подписки
            // (new-url) сразу после чтения панели, ещё до сборки, — так и осталось.
            return new EngineBuild(false, "Конфиг не собрался: " + ex.GetBaseException().Message, null, null, pool, []);
        }
    }

    /// <summary>
    /// Собирает и пишет конфиг движка, который при одном десинке только отвечает на DNS.
    /// </summary>
    /// <remarks>
    /// Когда — <see cref="AppSettings.NeedsDnsEngine"/>, как — <see cref="DnsEngine"/>.
    /// Пишется в тот же файл, что конфиг туннеля: при одном десинке тот
    /// не нужен, а следующий запуск с туннелем соберёт его заново.
    /// </remarks>
    public static EngineBuild BuildDnsOnly(AppSettings settings, EngineHost host)
    {
        try
        {
            var android = AndroidOf(host);
            var requestedDns = settings;
            var routerDns = host.RouterDns(settings);
            settings = routerDns.Settings;
            if (routerDns.Warning is not null) host.Journal("DNS", routerDns.Warning);

            // На телефоне приложения спрашивают DNS сети VPN — адрес DNS самого
            // TUN, — его и заводим в перехват; резолверов системы там не видно.
            var resolvers = android is null
                ? host.SystemResolvers()
                : [SingBoxConfigCompiler.TunDnsAddress(new SingBoxOptions().TunAddress) + "/32"];

            // Без адресов резолверов движку нечего ловить: TUN поднялся бы
            // впустую, а Windows спрашивала бы мимо него.
            if (resolvers.Count == 0)
                return Failed("DNS через движок не поднят: резолверы Windows не нашлись.");

            var addresses = AddressOverrides.Merge(new Dictionary<string, string>(), AddressOverrides.Load());
            var result = DnsEngine.Compile(settings, resolvers, addresses,
                android is null ? EngineKeys.Generate() : null, routerDns.Interface?.Name, android);

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);
            RouterDnsBuildState.Save(settings.ProxyConfigPath, requestedDns, routerDns.Warning);

            var message = settings.UseRouterDns
                ? $"DNS через движок: UDP/53 к роутеру {settings.RouterDnsAddress} через LAN."
                : android is null
                    ? $"DNS через движок: запросы Windows к {string.Join(", ", resolvers)} уходят на {settings.DnsServer} по DoH."
                    : $"DNS через движок: запросы приложений уходят на {settings.DnsServer} по DoH.";

            return new EngineBuild(true, message, routerDns.Warning, result, null, []);
        }
        catch (Exception ex)
        {
            return Failed("DNS через движок не поднят: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Параметры компилятора из собранного: то, что прежде стояло в окне
    /// инициализатором <c>new SingBoxOptions { … }</c>, без чтения файлов и сети.
    /// </summary>
    /// <remarks>
    /// Отдельно, чтобы сверить с прежним кодом окна байт в байт (тест
    /// <c>EngineConfigBuilderGoldenTests</c>): Windows-конфиг не должен
    /// измениться от переезда ни на символ.
    /// </remarks>
    public static SingBoxOptions TunnelOptions(AppSettings settings, EngineHost host, TunnelInputs inputs)
    {
        var android = AndroidOf(host);

        // Десинк — в движке (ByeDPI или нарезка) только без root: с root его
        // делает nfqws2 рядом, и маршрут остаётся direct, как на Windows.
        var inside = android is { Root: false } ? android : null;

        var resolvers = inputs.Narrow ? host.SystemResolvers() : Array.Empty<string>();

        // DNS сети VPN на телефоне — адрес в сети TUN (172.19.0.2). Выборочный
        // перехват заводит его явно: вводить ли подсеть самого TUN в маршруты,
        // решает VpnService, и полагаться на это не стоит. Не проверено на телефоне.
        if (android is not null && inputs.Narrow)
            resolvers = [.. resolvers, SingBoxConfigCompiler.TunDnsAddress(new SingBoxOptions().TunAddress) + "/32"];

        return new SingBoxOptions
        {
            Scope = inputs.Narrow ? TunnelScope.ProxyOnly : TunnelScope.Everything,
            Ipv6 = host.Ipv6(),
            DnsServerAddresses = resolvers,
            DnsServer = settings.UseRouterDns ? settings.RouterDnsAddress : settings.DnsServer,
            DnsServerType = settings.UseRouterDns ? "udp" : "https",
            RouterDns = settings.UseRouterDns,
            RouterDnsInterface = inputs.RouterDns.Interface?.Name,

            // Имя сертификата и путь — у провайдера из списка выбора.
            // Адрес, вписанный руками, идёт как прежде: без имени.
            DnsServerName = DnsSurvey.ByAddress(settings.DnsServer)?.TlsName,
            DnsServerPath = DnsSurvey.ByAddress(settings.DnsServer)?.DohPath,
            DnsThroughTunnel = !settings.UseRouterDns && settings.DnsVia == DnsRoute.Tunnel,
            DnsViaAuto = !settings.UseRouterDns && settings.DnsVia == DnsRoute.Auto,

            // Выбранный резолвер замолчит — надзор переведёт имена на запасной
            // (25.10: умер Comss DNS, и при «напрямую» висело всё).
            DnsSpare = DnsSpare.For(settings.DnsServer),

            // Все подписки из книги, а не одна действующая: переключаются
            // на другую как раз тогда, когда выход текущей лёг.
            PanelHosts = SubscriptionHosts.From([settings.SubscriptionUrl, .. inputs.PanelUrls]),
            PreferredServerTag = Warp.PreferredExit(settings),

            // Без «Selective Routing» и при WARP пулов нет — их маршруты
            // идут в пул по умолчанию.
            Pools = settings.EnginePools,

            // «Игнорировать исключения» сюда больше не передаётся:
            // с 23.09 она отменяет все прямые правила, а не одно
            // российское, и делает это режимом — ProxyStrict, где
            // правил нет вовсе. См. EngineChoice.Mode.
            ForeignExitsOnly = settings.ForeignExitsOnly,

            // Вход проверки — всегда, а не только под «Проверку прохода
            // трафика»: через него же идут замер скорости и проверка
            // блокировок через туннель (до 0.10.3 замер «через туннель»
            // падал — стучаться было не во что). На телефоне его нет вовсе:
            // слушатель на петле там видит любое приложение.
            HealthInbound = android is null,
            Keys = inputs.Keys,

            CaptureAddresses = inputs.Capture,
            DesyncAddresses = inputs.KeptOut,
            PinnedProxyAddresses = inputs.Pinned.Addresses,
            PinnedProxyNames = inputs.Pinned.Names,
            AddressOverrides = inputs.Overrides,

            // Мёртвые — мимо автоподбора, но в селекторе остаются:
            // закрепить такой сервер руками законное желание, он мог
            // подняться между нашими замерами. Убранные человеком — так же.
            DeadServerTags = inputs.Dead
                .Concat(settings.AutoPickExcluded)
                .ToHashSet(StringComparer.Ordinal),

            // Отвечающие через раз (ServerHealth.Flaky) — в автоподборе,
            // но после стабильных, по выключателю «Сначала стабильные».
            FlakyServerTags = settings.StableFirst
                ? inputs.Health.Flaky().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),

            // Проверка серверов — как настроил человек (AppSettings).
            LatencyTestInterval = $"{Math.Max(1, settings.FullCheckMinutes)}m",

            // Адрес проверки — один на всех (Ping, 07.10).
            LatencyTestUrl = Ping.UrlOf(settings),
            TlsFragment = settings.TlsFragment,
            StoreFakeIp = settings.FakeIpMemory,
            AutoPickPerEntry = Math.Max(0, settings.AutoPickPerEntry),
            KnownLatency = inputs.Health.Latencies(),

            // Телефон. На Windows всё ниже — значения по умолчанию.
            Platform = host.Platform,
            HostsFilePath = android?.HostsFilePath,
            ExcludePackages = android?.ExcludePackages ?? [],
            IncludePackages = android?.IncludePackages ?? [],
            DesyncSocks = inside?.Socks,
            DesyncPassHosts = inside?.Socks is not null ? inside.DesyncPassHosts : [],
            DesyncFragment = inside is { Socks: null } ? inside.DesyncFragment : TlsFragment.Off,
            DesyncFragmentFallbackDelay = inside?.DesyncFragmentFallbackDelay,
            DesyncRejectQuic = inside?.DesyncRejectQuic ?? false,
            DesyncDefault = inside is not null
                && (inside.Socks is not null ? inside.DesyncSocksDefault : inside.DesyncFragmentDefault),
        };
    }

    /// <summary>Телефонная часть хозяина; <c>null</c> — Windows.</summary>
    private static AndroidEngine? AndroidOf(EngineHost host) =>
        host.Platform != SingBoxPlatform.Android ? null
        : host.Android ?? throw new ArgumentException("Сборке для телефона нужен EngineHost.Android: путь файла пинов и остальное.");

    private static EngineBuild Failed(string message) => new(false, message, null, null, null, []);
}
