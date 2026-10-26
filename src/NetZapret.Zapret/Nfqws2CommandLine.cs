using System.Globalization;
using System.Text;
using NetZapret.Core;

namespace NetZapret.Zapret;

/// <summary>
/// Где и от чьего имени работает nfqws2 на телефоне с root. Заполняет хозяин Android.
/// </summary>
public sealed record Nfqws2Host
{
    /// <summary><c>&lt;files&gt;/zapret</c> — рабочая папка nfqws2: от неё пути пресета.</summary>
    public required string ZapretRoot { get; init; }

    /// <summary><c>&lt;nativeLibraryDir&gt;/libnfqws2.so</c>.</summary>
    public required string Executable { get; init; }

    /// <summary>uid приложения (<c>Android.OS.Process.MyUid()</c>): к нему nfqws2 сбрасывает права.</summary>
    public required int Uid { get; init; }

    /// <summary><c>&lt;files&gt;/runtime/nfqws2.pid</c>, создан приложением заранее.</summary>
    public required string PidFile { get; init; }

    /// <summary><c>&lt;files&gt;/runtime/nfqws2.log</c>, создан приложением заранее.</summary>
    public required string LogFile { get; init; }

    public ushort QueueNumber { get; init; } = Nfqws2CommandLine.DefaultQueue;

    public uint DesyncMark { get; init; } = Nfqws2CommandLine.DefaultMark;

    /// <summary><c>--bind-fix4</c> и <c>--bind-fix6</c> (см. <see cref="Nfqws2CommandLine.LinuxGlobals"/>).</summary>
    public bool BindFix { get; init; } = true;

    /// <summary><c>null</c> либо <c>"android"</c> — подробный журнал в logcat, только для разбора.</summary>
    public string? Debug { get; init; }
}

/// <summary>Части фильтра WinDivert пресетов (<c>--wf-raw-part</c>), переведённые в правила xt_u32.</summary>
public enum RawPart
{
    Stun,
    DiscordMedia,
    WireGuard,
}

/// <summary>Сколько пакетов соединения отдаётся nfqws2, когда в ядре есть xt_connbytes.</summary>
public sealed record CaptureBudget(int TcpOut = 20, int UdpOut = 20, int TcpIn = 10, int UdpIn = 10);

/// <summary>Что ключи <c>--wf-*</c> просили перехватить, без привязки к WinDivert.</summary>
public sealed record CapturePlan
{
    public IReadOnlyList<(int First, int Last)> TcpOut { get; init; } = [];
    public IReadOnlyList<(int First, int Last)> UdpOut { get; init; } = [];

    /// <summary><c>--wf-tcp-in</c>: только «shadow probes» и «white sni».</summary>
    public IReadOnlyList<(int First, int Last)> TcpIn { get; init; } = [];

    /// <summary><c>--wf-udp-in</c>.</summary>
    public IReadOnlyList<(int First, int Last)> UdpIn { get; init; } = [];

    public IReadOnlySet<RawPart> RawParts { get; init; } = new HashSet<RawPart>();

    /// <summary><c>--wf-filter-lan</c>, по умолчанию 1.</summary>
    public bool FilterLan { get; init; } = true;

    /// <summary><c>--wf-filter-loopback</c>, по умолчанию 1.</summary>
    public bool FilterLoopback { get; init; } = true;

    /// <summary><c>--wf-l3</c>.</summary>
    public bool Ipv4 { get; init; } = true;

    public bool Ipv6 { get; init; } = true;

    public CaptureBudget Budget { get; init; } = new();

    /// <summary>Ключи только для Windows, убранные без замены, — для журнала.</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];

    /// <summary>Секции, чей <c>--out-range</c>/<c>--in-range</c> длиннее бюджета, — для журнала.</summary>
    public IReadOnlyList<string> BeyondBudget { get; init; } = [];
}

/// <summary>Готовая строка nfqws2 и что перехватывать правилами iptables.</summary>
public sealed record Nfqws2Launch(IReadOnlyList<string> Arguments, CapturePlan Capture);

/// <summary>
/// Командная строка nfqws2 bol-van v1.0.6 — десинк на телефоне с root
/// (заметка <c>nfqws2.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// Строка та же, что у winws2 на Windows, — её собирает тот же
/// <see cref="WinwsCommandLine.Build"/>: щит, свои рецепты, копии исключений,
/// ширина перехвата, game filter. Правка там доходит до телефона пересборкой.
/// Здесь только перевод: ключи <c>--wf-*</c> nfqws2 не знает вовсе — с любым
/// из них он печатает справку и выходит (<c>getopt_long_only</c> → <c>?</c>,
/// <c>nfqws.c</c>), — поэтому они убираются, а что они просили перехватить,
/// становится правилами iptables (<see cref="NfqueueFirewall"/>).
/// </para>
/// <para>
/// Исключения туннеля (серверы, подменные адреса, UDP Riot, виртуальные
/// локалки) в строку не идут: на Android это правила <c>RETURN</c> в iptables,
/// без предела WinDivert в сорок адресов.
/// </para>
/// </remarks>
public static class Nfqws2CommandLine
{
    /// <summary>
    /// Номер очереди: не 200 и не 300, которые берут nzapret, magisk-zapret2
    /// и скрипты zapret, — чтобы два обхода на одном телефоне не делили очередь.
    /// </summary>
    public const ushort DefaultQueue = 1717;

    /// <summary>
    /// Метка своих пакетов: умолчание nfqws2 и тех же модулей — их подделки
    /// друг другом не обрабатываются. Бит вне раскладки меток Android
    /// (netId 0–15, флаги 16–20) — по описанию netd, на ядрах производителей
    /// не проверено.
    /// </summary>
    public const uint DefaultMark = 0x40000000;

    /// <summary>Потолок бюджета пакетов на соединение.</summary>
    public const int BudgetCeiling = 64;

    /// <summary>Строка nfqws2 из пресета: общая с Windows сборка и перевод.</summary>
    public static Nfqws2Launch Build(
        ZapretPreset preset,
        Nfqws2Host host,
        string? excludeList = null,
        IReadOnlyList<OwnDesyncProfile>? own = null,
        bool gameFilter = false,
        CaptureWidth capture = CaptureWidth.Preset,
        string? keepList = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(host);

        var winws = WinwsCommandLine.Build(
            preset, excludeList, own, gameFilter,
            tunnelServers: null, tunnelFakeRange: null, udpOff: null, tunAddresses: null,
            capture: capture, keepList: keepList, lans: null);

        var (arguments, plan) = Translate(winws);

        // Общие ключи от места не зависят — вперёд, чтобы их было видно в журнале.
        arguments.InsertRange(0, LinuxGlobals(host));

        var budget = BudgetFor(arguments, out var beyond);
        return new Nfqws2Launch(arguments, plan with { Budget = budget, BeyondBudget = beyond });
    }

    /// <summary>
    /// Убирает ключи только для Windows; что они просили перехватить — в <see cref="CapturePlan"/>.
    /// </summary>
    /// <remarks>
    /// Прочие ключи остаются нетронутыми и на своих местах — то же правило, что
    /// у <see cref="WinwsCommandLine"/>: незнакомый ключ должен дойти до движка.
    /// </remarks>
    public static (List<string> Arguments, CapturePlan Plan) Translate(IReadOnlyList<string> winwsArguments)
    {
        ArgumentNullException.ThrowIfNull(winwsArguments);

        var arguments = new List<string>();
        var tcpOut = new List<(int, int)>();
        var udpOut = new List<(int, int)>();
        var tcpIn = new List<(int, int)>();
        var udpIn = new List<(int, int)>();
        var raw = new HashSet<RawPart>();
        var dropped = new List<string>();
        bool filterLan = true, filterLoopback = true, ipv4 = true, ipv6 = true;
        bool sawPorts = false, sawRaw = false;

        foreach (var argument in winwsArguments)
        {
            if (!IsWindowsOnly(argument))
            {
                arguments.Add(argument);
                continue;
            }

            var (key, value) = Split(argument);

            switch (key.ToLowerInvariant())
            {
                case "--wf-tcp-out":
                    tcpOut.AddRange(Ports(value));
                    sawPorts = true;
                    break;

                case "--wf-udp-out":
                    udpOut.AddRange(Ports(value));
                    sawPorts = true;
                    break;

                case "--wf-tcp-in":
                    tcpIn.AddRange(Ports(value));
                    break;

                case "--wf-udp-in":
                    udpIn.AddRange(Ports(value));
                    break;

                case "--wf-raw-part":
                    switch (Path.GetFileName(value.Trim().TrimStart('@').Replace('\\', '/')).ToLowerInvariant())
                    {
                        case "windivert_part.stun.txt":
                            raw.Add(RawPart.Stun);
                            break;
                        case "windivert_part.discord_media.txt":
                            raw.Add(RawPart.DiscordMedia);
                            break;
                        case "windivert_part.wireguard.txt":
                            raw.Add(RawPart.WireGuard);
                            break;
                        default:
                            dropped.Add($"часть фильтра {value} на Android не переводится");
                            break;
                    }

                    break;

                case "--wf-raw":
                    sawRaw = true;
                    dropped.Add("полный фильтр WinDivert (--wf-raw) на Android не переводится — перехват по портам «всё»");
                    break;

                case "--wf-filter-lan":
                    filterLan = value.Trim() != "0";
                    break;

                case "--wf-filter-loopback":
                    filterLoopback = value.Trim() != "0";
                    break;

                case "--wf-l3":
                    var families = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    ipv4 = families.Any(f => f.Equals("ipv4", StringComparison.OrdinalIgnoreCase));
                    ipv6 = families.Any(f => f.Equals("ipv6", StringComparison.OrdinalIgnoreCase));
                    break;

                case "--wf-save":
                case "--wf-dup-check":
                    break;

                default:
                    dropped.Add($"{key} на Android не переводится");
                    break;
            }
        }

        // Полный фильтр без портов — ширина «всё» (PresetCapture, CaptureWidth.All).
        if (sawRaw && !sawPorts && PresetCapture.PortsOf(CaptureWidth.All) is { } all)
        {
            tcpOut.AddRange(Ports(all.Tcp));
            udpOut.AddRange(Ports(all.Udp));
            sawPorts = true;
        }

        // winws2 такой пресет не принял бы вовсе («must specify port or/and
        // partial raw filter»); на телефоне — порты по умолчанию и запись в журнал.
        if (!sawPorts && raw.Count == 0)
        {
            tcpOut.AddRange(Ports(PresetPorts.Default));
            udpOut.AddRange(Ports("443"));
            dropped.Add($"у пресета нет портов перехвата — взяты TCP {PresetPorts.Default}, UDP 443");
        }

        var plan = new CapturePlan
        {
            TcpOut = Merge(tcpOut),
            UdpOut = Merge(udpOut),
            TcpIn = Merge(tcpIn),
            UdpIn = Merge(udpIn),
            RawParts = raw,
            FilterLan = filterLan,
            FilterLoopback = filterLoopback,
            Ipv4 = ipv4,
            Ipv6 = ipv6,
            Dropped = dropped,
        };

        return (arguments, plan);
    }

    /// <summary>Ключ только для Windows: <c>--wf-*</c> и фильтры сетей Windows.</summary>
    /// <remarks>
    /// <c>--filter-ssid</c> — ключ профиля и на Linux есть; под это правило он
    /// не попадает: здесь только <c>--ssid-filter…</c> и <c>--nlm-…</c> (глобальные,
    /// под <c>#ifdef __CYGWIN__</c>).
    /// </remarks>
    public static bool IsWindowsOnly(string argument)
    {
        if (argument.StartsWith("--wf-", StringComparison.OrdinalIgnoreCase))
            return true;

        var (key, _) = Split(argument);

        return key.ToLowerInvariant() is "--ssid-filter" or "--ssid-filter-neg"
            or "--nlm-filter" or "--nlm-filter-neg" or "--nlm-list";
    }

    /// <summary>
    /// Общие ключи Linux — встают в начало строки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--uid</c> — uid приложения, не <c>0x7FFFFFFF</c> и не <c>0:0</c>: после
    /// сброса прав у процесса нет <c>CAP_DAC_OVERRIDE</c>, а списки, lua и образцы
    /// лежат в закрытой папке приложения и читаются уже после сброса
    /// (<c>test_list_files</c>, lua, перечитывание списков по времени). Читать их
    /// может только сам uid приложения — зато «Сайт не открывается?» правит
    /// <c>desync-exclude.txt</c>, и nfqws2 перечитывает его сам, как winws2.
    /// </para>
    /// <para>
    /// <c>--bind-fix4/6</c> обязательны: у Android маршрут выбирают правила по uid,
    /// и пакет от uid приложения без метки «мимо VPN» мог бы уйти в tun0;
    /// <c>SO_BINDTODEVICE</c> к исходному интерфейсу держит его на wlan0/rmnet.
    /// Так же у nzapret. На телефоне не проверено.
    /// </para>
    /// <para>
    /// Не ставятся <c>--daemon</c> (закрывает 0–2 — журнал пропал бы), <c>--user</c>,
    /// <c>--filter-mark</c> и <c>@файл</c> (на Android не поддержан).
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> LinuxGlobals(Nfqws2Host host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var globals = new List<string>
        {
            $"--qnum={host.QueueNumber.ToString(CultureInfo.InvariantCulture)}",
            $"--fwmark={Mark(host.DesyncMark)}",
            $"--uid={host.Uid.ToString(CultureInfo.InvariantCulture)}:{host.Uid.ToString(CultureInfo.InvariantCulture)}",
            $"--pidfile={host.PidFile}",
        };

        if (host.BindFix)
            globals.AddRange(["--bind-fix4", "--bind-fix6"]);

        if (string.Equals(host.Debug, "android", StringComparison.OrdinalIgnoreCase))
            globals.Add("--debug=android");

        return globals;
    }

    /// <summary>Метка в записи nfqws2 и iptables: <c>0x40000000</c>.</summary>
    public static string Mark(uint mark) => "0x" + mark.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Сколько пакетов соединения отдавать nfqws2 — по <c>--out-range</c>/<c>--in-range</c> секций.
    /// </summary>
    /// <remarks>
    /// <para>
    /// На Windows WinDivert отдаёт winws2 все пакеты перехваченных портов, а здесь
    /// счётчик xt_connbytes режет поток на первых N — иначе однопоточный
    /// nfqws2 пропускал бы через себя каждый байт отдачи. По умолчанию 20/20/10/10,
    /// как в <c>config.default</c> zapret и у magisk-zapret2; секции с окном
    /// шире поднимают бюджет до <see cref="BudgetCeiling"/>.
    /// </para>
    /// <para>
    /// Оценка в пакетах: у TCP <c>nN</c> — N, <c>dN</c> — 2N+2 (пакеты с данными
    /// чередуются с ACK, плюс SYN и ACK рукопожатия), <c>sN</c> — 2·⌈N/1200⌉+4;
    /// у UDP <c>n</c>/<c>d</c> — N, <c>s</c> — ⌈N/1200⌉+2. Секции с <c>a</c>
    /// (всегда) и с оценкой выше потолка — в <paramref name="beyond"/>: на
    /// Android они получат только первые пакеты.
    /// </para>
    /// </remarks>
    public static CaptureBudget BudgetFor(IReadOnlyList<string> arguments, out IReadOnlyList<string> beyond)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var defaults = new CaptureBudget();
        int tcpOut = defaults.TcpOut, udpOut = defaults.UdpOut, tcpIn = defaults.TcpIn, udpIn = defaults.UdpIn;
        var past = new List<string>();

        foreach (var section in Sections(arguments))
        {
            bool tcp = new ZapretSection
            {
                Name = string.Empty,
                HostListPaths = [],
                InlineDomains = [],
                IpSetPaths = [],
                DesyncRecipes = [],
                RawArguments = section,
            }.CarriesTcp;

            var name = section.FirstOrDefault(a => a.StartsWith("--name=", StringComparison.Ordinal))?["--name=".Length..]
                ?? "без имени";

            foreach (var argument in section)
            {
                bool outgoing = argument.StartsWith("--out-range=", StringComparison.Ordinal);
                bool incoming = argument.StartsWith("--in-range=", StringComparison.Ordinal);

                if (!outgoing && !incoming)
                    continue;

                var estimate = Estimate(argument[(argument.IndexOf('=') + 1)..], tcp);

                if (estimate is null)
                    continue;

                if (estimate.Value > BudgetCeiling && !past.Contains(name))
                    past.Add(name);

                // «Всегда» бюджет не поднимает: потолок ему всё равно мал, а
                // ради одной секции (у V12 Lite — «Discord UDP», udplen на
                // каждом пакете) весь UDP перехваченных портов шёл бы по 64
                // пакета на поток.
                if (estimate.Value == int.MaxValue)
                    continue;

                int value = Math.Clamp(estimate.Value, 0, BudgetCeiling);

                if (outgoing && tcp)
                    tcpOut = Math.Max(tcpOut, value);
                else if (outgoing)
                    udpOut = Math.Max(udpOut, value);
                else if (tcp)
                    tcpIn = Math.Max(tcpIn, value);
                else
                    udpIn = Math.Max(udpIn, value);
            }
        }

        beyond = past;
        return new CaptureBudget(tcpOut, udpOut, tcpIn, udpIn);
    }

    /// <summary>Пакетов на диапазон; <c>int.MaxValue</c> — «всегда»; <c>null</c> — нечего считать.</summary>
    private static int? Estimate(string range, bool tcp)
    {
        var text = range.Trim();

        if (text.Length == 0)
            return null;

        // Верхняя граница: после «-» или «<», а одиночное значение — само себе граница.
        int split = Math.Max(text.LastIndexOf('-'), text.LastIndexOf('<'));
        var upper = split >= 0 ? text[(split + 1)..] : text;

        if (upper.Length == 0)
            return null;

        char mode = char.ToLowerInvariant(upper[0]);

        if (mode == 'a')
            return int.MaxValue;

        if (mode == 'x')
            return null;

        if (!long.TryParse(upper[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return null;

        long packets = (mode, tcp) switch
        {
            ('n', _) => n,
            ('d', true) => 2 * n + 2,
            ('d', false) => n,
            ('s', true) => 2 * ((n + 1199) / 1200) + 4,
            ('s', false) => (n + 1199) / 1200 + 2,
            _ => -1,
        };

        return packets < 0 ? null : (int)Math.Min(packets, int.MaxValue);
    }

    /// <summary>Профили строки: куски между <c>--new</c>.</summary>
    private static IEnumerable<List<string>> Sections(IReadOnlyList<string> arguments)
    {
        var current = new List<string>();

        foreach (var argument in arguments)
        {
            if (argument.Equals("--new", StringComparison.Ordinal))
            {
                yield return current;
                current = [];
                continue;
            }

            current.Add(argument);
        }

        yield return current;
    }

    /// <summary>
    /// Строка проверки без root: без <c>--uid</c> и <c>--pidfile</c>, с <c>--dry-run</c>.
    /// </summary>
    /// <remarks>
    /// Проверяет разбор ключей, образцы и списки, lua не грузит (руководство:
    /// «--dry-run does not initialize the Lua engine»). Ожидаемый конец вывода —
    /// «command line parameters verified», код 0.
    /// </remarks>
    public static IReadOnlyList<string> DryRun(IReadOnlyList<string> arguments) =>
        arguments
            .Where(a => !a.StartsWith("--uid=", StringComparison.Ordinal) && !a.StartsWith("--pidfile=", StringComparison.Ordinal))
            .Append("--dry-run")
            .ToList();

    /// <summary>
    /// Строка проверки lua через root: без <c>--pidfile</c>, с <c>--intercept=0</c>.
    /// </summary>
    /// <remarks>
    /// Грузит все <c>--lua-init</c> и проверяет, что функции рецептов есть, но
    /// очередь не трогает — можно рядом с работающим. Если lua завела таймеры,
    /// процесс не выйдет сам: держать под сроком в 10 с.
    /// </remarks>
    public static IReadOnlyList<string> LuaCheck(IReadOnlyList<string> arguments) =>
        arguments
            .Where(a => !a.StartsWith("--pidfile=", StringComparison.Ordinal))
            .Append("--intercept=0")
            .ToList();

    /// <summary>Слово для sh в одинарных кавычках: <c>'</c> → <c>'\''</c>.</summary>
    /// <remarks>
    /// Каждый аргумент — в кавычках: <c>--filter-tcp=*</c> раскрылся бы в имена
    /// файлов, а в именах секций пробелы, кириллица, <c>&amp;</c> и скобки.
    /// </remarks>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Сценарий запуска nfqws2 для <c>su -c sh &lt;файл&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок: перейти в корень Zapret (пути пресета относительные, а образцы
    /// и lua nfqws2 читает ещё при разборе ключей), снять свои правила
    /// (<paramref name="firewallDownScriptPath"/>; иначе <c>--noflush</c> добавил
    /// бы второй переход в цепочку), поставить новые
    /// (<paramref name="firewallUpScriptPath"/>), и <c>exec</c> nfqws2. Правила
    /// раньше процесса безопасны: у каждого NFQUEUE <c>--queue-bypass</c>, пока
    /// очередь никто не слушает, пакеты идут мимо.
    /// </para>
    /// <para>
    /// <c>exec</c>, а не фон: номер процесса оболочки становится номером nfqws2
    /// (он и печатается строкой <c>NZ_PID</c>), а сама команда <c>su</c> живёт,
    /// пока живёт nfqws2, — хозяину есть за чем следить. Готовность — непустой
    /// pid-файл: nfqws2 пишет его только после того, как занял очередь.
    /// Нужен фон (общая долгоживущая оболочка root) — <c>sh файл &amp;</c>.
    /// </para>
    /// <para>
    /// Сторож в фоне — аналог задания Windows (<c>ProcessJob</c>), с которым
    /// winws2 умирает вместе с надзором: процесс приложения пропал — снять
    /// правила и остановить nfqws2. Перенос в корневую группу cgroup — чтобы
    /// заморозка кэшированных приложений не остановила очередь. Ни сторож,
    /// ни перенос на телефоне не проверены.
    /// </para>
    /// </remarks>
    public static string StartScript(
        Nfqws2Host host,
        IReadOnlyList<string> arguments,
        int appPid,
        string packageName,
        string firewallDownScriptPath,
        string? firewallUpScriptPath = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(arguments);

        var log = Quote(host.LogFile);
        var script = new StringBuilder();

        script.Append("#!/system/bin/sh\n");
        script.Append("# NetZapret: запуск nfqws2, пишется заново перед каждым запуском.\n");
        script.Append($"cd {Quote(host.ZapretRoot)} || exit 90\n");
        script.Append($"sh {Quote(firewallDownScriptPath)} >/dev/null 2>&1\n");

        if (firewallUpScriptPath is not null)
        {
            script.Append($"if ! sh {Quote(firewallUpScriptPath)} >>{log} 2>&1; then\n");
            script.Append($"  echo 'NZ_FAIL firewall' ; sh {Quote(firewallDownScriptPath)} >/dev/null 2>&1; exit 91\n");
            script.Append("fi\n");
        }

        script.Append($"echo \"=== NetZapret nfqws2 start $(date '+%Y-%m-%d %H:%M:%S') ===\" >>{log}\n");
        script.Append("NFQ=$$\n");
        script.Append("echo \"NZ_PID $NFQ\"\n");
        script.Append("echo $NFQ > /sys/fs/cgroup/cgroup.procs 2>/dev/null || echo $NFQ > /acct/cgroup.procs 2>/dev/null\n");
        script.Append($"( APP={appPid.ToString(CultureInfo.InvariantCulture)}\n");
        script.Append("  while [ -d /proc/$NFQ ]; do\n");
        script.Append($"    case \"$(cat /proc/$APP/cmdline 2>/dev/null)\" in {Quote(packageName)}*) sleep 3 ;; *) break ;; esac\n");
        script.Append("  done\n");
        script.Append("  if [ -d /proc/$NFQ ]; then\n");
        script.Append($"    sh {Quote(firewallDownScriptPath)} >/dev/null 2>&1\n");
        script.Append("    kill -TERM $NFQ 2>/dev/null; sleep 2; kill -KILL $NFQ 2>/dev/null\n");
        script.Append("  fi ) </dev/null >/dev/null 2>&1 &\n");
        script.Append("echo \"NZ_SENTINEL $!\"\n");
        script.Append("exec ").Append(Quote(host.Executable));

        foreach (var argument in arguments)
            script.Append(" \\\n  ").Append(Quote(argument));

        script.Append($" \\\n  </dev/null >>{log} 2>&1\n");
        return script.ToString();
    }

    /// <summary>
    /// Сценарий остановки: сначала правила, потом процесс по pid-файлу.
    /// </summary>
    /// <remarks>
    /// Порядок zapret (<c>zapret_unapply_firewall</c>, затем <c>do_nfqws 0</c>).
    /// <c>SIGTERM</c> — nfqws2 отвязывает очередь и выходит с 0; через 3 с —
    /// <c>SIGKILL</c>. Убивается только наш процесс: номер из pid-файла
    /// сверяется с исполняемым файлом — после перезагрузки номер мог достаться
    /// чужому. Приложение само послать сигнал не может (другой домен SELinux,
    /// <c>hidepid</c>) — только через root.
    /// </remarks>
    public static string StopScript(Nfqws2Host host, string firewallDownScriptPath)
    {
        ArgumentNullException.ThrowIfNull(host);

        var pid = Quote(host.PidFile);
        var script = new StringBuilder();

        script.Append("#!/system/bin/sh\n");
        script.Append("# NetZapret: остановка nfqws2 — правила, затем процесс.\n");
        script.Append($"sh {Quote(firewallDownScriptPath)} >/dev/null 2>&1\n");
        script.Append($"P=$(cat {pid} 2>/dev/null)\n");
        script.Append("case \"$P\" in ''|*[!0-9]*) P= ;; esac\n");
        script.Append("if [ -n \"$P\" ]; then\n");
        script.Append("  case \"$(readlink /proc/$P/exe 2>/dev/null)\" in *libnfqws2.so*|*nfqws2*) kill -TERM $P 2>/dev/null ;; *) P= ;; esac\n");
        script.Append("fi\n");
        script.Append("if [ -n \"$P\" ]; then\n");
        script.Append("  for i in 1 2 3 4 5 6; do [ -d /proc/$P ] || break; sleep 0.5; done\n");
        script.Append("  [ -d /proc/$P ] && kill -KILL $P 2>/dev/null\n");
        script.Append("fi\n");
        script.Append($": > {pid}\n");
        script.Append("exit 0\n");
        return script.ToString();
    }

    /// <summary>
    /// Все файлы запуска с root: правила, их снятие и постановка, запуск и остановка.
    /// </summary>
    /// <param name="runtimeDirectory">Куда хозяин запишет файлы (<c>&lt;files&gt;/runtime</c>).</param>
    /// <param name="appPid">Номер процесса приложения — за ним следит сторож.</param>
    /// <param name="packageName">Имя пакета — сторож сверяет его с <c>/proc/&lt;pid&gt;/cmdline</c>.</param>
    public static Nfqws2Scripts Scripts(
        Nfqws2Host host,
        Nfqws2Launch launch,
        FirewallExclusions exclusions,
        KernelFeatures kernel,
        string runtimeDirectory,
        int appPid,
        string packageName)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(kernel);

        string File(string name) => Join(runtimeDirectory, name);

        var warnings = new List<string>(NfqueueFirewall.Warnings(launch.Capture, kernel));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var rules4Path = File(Nfqws2Scripts.Rules4Name);
        var rules6Path = File(Nfqws2Scripts.Rules6Name);
        var downPath = File(Nfqws2Scripts.DownName);
        var upPath = File(Nfqws2Scripts.UpName);
        var startPath = File(Nfqws2Scripts.StartName);
        var stopPath = File(Nfqws2Scripts.StopName);

        bool v4 = launch.Capture.Ipv4 && kernel.Mangle4 && kernel.Nfqueue4;
        bool v6 = launch.Capture.Ipv6 && kernel.Mangle6 && kernel.Nfqueue6;

        string? rules4 = v4
            ? NfqueueFirewall.RestoreBatch(System.Net.Sockets.AddressFamily.InterNetwork, launch.Capture, exclusions, kernel, host.QueueNumber, host.DesyncMark)
            : null;

        string? rules6 = v6
            ? NfqueueFirewall.RestoreBatch(System.Net.Sockets.AddressFamily.InterNetworkV6, launch.Capture, exclusions, kernel, host.QueueNumber, host.DesyncMark)
            : null;

        if (rules4 is not null)
            files[Nfqws2Scripts.Rules4Name] = rules4;

        if (rules6 is not null)
            files[Nfqws2Scripts.Rules6Name] = rules6;

        files[Nfqws2Scripts.DownName] = NfqueueFirewall.DownScript();
        files[Nfqws2Scripts.UpName] = NfqueueFirewall.ApplyScript(
            rules4 is null ? null : rules4Path, rules4,
            rules6 is null ? null : rules6Path, rules6,
            kernel.RestoreWait);
        files[Nfqws2Scripts.StartName] = StartScript(host, launch.Arguments, appPid, packageName, downPath, upPath);
        files[Nfqws2Scripts.StopName] = StopScript(host, downPath);

        return new Nfqws2Scripts(
            files,
            ["su", "-c", "sh " + Quote(startPath)],
            ["su", "-c", "sh " + Quote(stopPath)],
            warnings);
    }

    /// <summary>Путь на телефоне: всегда «/», даже когда сборка идёт на Windows.</summary>
    private static string Join(string directory, string name) =>
        directory.TrimEnd('/', '\\') + "/" + name;

    private static (string Key, string Value) Split(string argument)
    {
        int eq = argument.IndexOf('=');
        int space = argument.IndexOf(' ');
        int at = eq >= 0 ? eq : space;

        return at < 0 ? (argument.Trim(), string.Empty) : (argument[..at].Trim(), argument[(at + 1)..].Trim());
    }

    /// <summary>
    /// Порты winws2 диапазонами; <c>~N[-M]</c> — всё, кроме них.
    /// </summary>
    /// <remarks>
    /// <see cref="PresetCapture.Ranges"/> отрицание пропускает (там оно сужает
    /// объявление секции); для перехвата оно значит дополнение. Ни один пресет
    /// его сейчас не пишет.
    /// </remarks>
    private static IEnumerable<(int First, int Last)> Ports(string value)
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith('~'))
            {
                foreach (var range in PresetCapture.Ranges(part))
                    yield return range;

                continue;
            }

            foreach (var (first, last) in PresetCapture.Ranges(part[1..]))
            {
                if (first > 1)
                    yield return (1, first - 1);

                if (last < 65535)
                    yield return (last + 1, 65535);
            }
        }
    }

    /// <summary>Сортирует, сливает пересекающиеся и смежные, выбрасывает порт 0.</summary>
    internal static List<(int First, int Last)> Merge(IEnumerable<(int First, int Last)> ranges)
    {
        var sorted = ranges
            .Select(r => (First: Math.Max(r.First, 1), Last: Math.Min(r.Last, 65535)))
            .Where(r => r.First <= r.Last)
            .OrderBy(r => r.First)
            .ThenBy(r => r.Last)
            .ToList();

        var merged = new List<(int First, int Last)>();

        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.First <= merged[^1].Last + 1)
                merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, range.Last));
            else
                merged.Add(range);
        }

        return merged;
    }
}

/// <summary>Файлы запуска nfqws2 с root и команды для хозяина.</summary>
/// <param name="Files">Имя файла в папке runtime → содержимое (UTF-8 без BOM, <c>\n</c>).</param>
/// <param name="Start">Команда запуска: <c>su -c sh '…/nfqws2-start.sh'</c>.</param>
/// <param name="Stop">Команда остановки: <c>su -c sh '…/nfqws2-stop.sh'</c>.</param>
/// <param name="Warnings">Что ядро не умеет и чем это обернётся.</param>
public sealed record Nfqws2Scripts(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Start,
    IReadOnlyList<string> Stop,
    IReadOnlyList<string> Warnings)
{
    public const string Rules4Name = "nfqws2-fw4.rules";
    public const string Rules6Name = "nfqws2-fw6.rules";
    public const string DownName = "nfqws2-fw-down.sh";
    public const string UpName = "nfqws2-fw-up.sh";
    public const string StartName = "nfqws2-start.sh";
    public const string StopName = "nfqws2-stop.sh";
}
