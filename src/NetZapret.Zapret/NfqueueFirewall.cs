using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetZapret.Zapret;

/// <summary>
/// Что выводится из перехвата nfqws2 — то же, что на Windows выводит из фильтра
/// WinDivert <see cref="TunnelCapture"/>.
/// </summary>
public sealed record FirewallExclusions
{
    /// <summary>Серверы туннеля — тот же <c>TunnelEndpoints.Read</c>, что у надзора Windows.</summary>
    public IReadOnlyList<IPAddress> Servers { get; init; } = [];

    /// <summary>Подменные адреса туннеля (<c>198.18.0.0/15</c>); <c>null</c> — нет.</summary>
    public string? FakeRange { get; init; }

    /// <summary>Сети, UDP к которым десинку не отдаётся (<see cref="UdpOffDesync"/>, игровой UDP Riot).</summary>
    public IReadOnlyList<string> UdpOff { get; init; } = [];

    /// <summary>Виртуальные локалки Радмина и Hamachi (<c>LocalNetworks.VirtualLans</c>).</summary>
    public IReadOnlyList<string> Lans { get; init; } = [];
}

/// <summary>
/// Что умеет netfilter телефона — по пробе <see cref="NfqueueFirewall.ProbeScript"/>.
/// </summary>
public sealed record KernelFeatures
{
    /// <summary>Таблица mangle и сопоставление <c>-m mark</c> — без них правил нет вовсе.</summary>
    public bool Mangle4 { get; init; }
    public bool Mangle6 { get; init; }

    public bool Nfqueue4 { get; init; }
    public bool Nfqueue6 { get; init; }

    public bool Connbytes4 { get; init; }
    public bool Connbytes6 { get; init; }

    public bool U32_4 { get; init; }
    public bool U32_6 { get; init; }

    /// <summary>Цель CONNMARK, сопоставление connmark и icmp в таблице filter.</summary>
    public bool Connmark4 { get; init; }
    public bool Connmark6 { get; init; }

    /// <summary>Только для сведения: правила multiport не используют.</summary>
    public bool Multiport { get; init; }

    /// <summary><c>iptables-restore</c> принимает <c>-w</c>.</summary>
    public bool RestoreWait { get; init; }

    /// <summary>Есть <c>/proc/net/netfilter/nfnetlink_queue</c>.</summary>
    public bool QueueModule { get; init; }
}

/// <summary>
/// Правила iptables для nfqws2 — только текст; выполняет его хозяин через root
/// (заметка <c>nfqws2.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// Своя цепочка в mangle <c>OUTPUT</c> и <c>INPUT</c>, дописанная в конец
/// (<c>-A</c>), а не вставленная: вердикт NFQUEUE «пропустить» выводит пакет
/// из таблицы, и цепочки Android (<c>connmark_mangle_*</c> — номер сети в метке
/// соединения) для него бы не сработали.
/// </para>
/// <para>
/// Сперва «кандидат ли пакет» (порт, первые пакеты соединения), потом
/// исключения: длинный список исключений проходят только кандидаты. Без
/// <c>multiport</c> и без <c>ipset</c> — их у стоковых Android может не быть:
/// правило на диапазон портов, правило на адрес. У каждого NFQUEUE
/// <c>--queue-bypass</c>: умер nfqws2 — трафик идёт без десинка, а не встаёт.
/// </para>
/// </remarks>
public static class NfqueueFirewall
{
    public const string Out = "netzapret_out", OutQ = "netzapret_outq",
                        In = "netzapret_in", InQ = "netzapret_inq",
                        Icmp = "netzapret_icmp", Probe = "netzapret_probe";

    /// <summary>Сколько серверов туннеля выводится правилами (у WinDivert было сорок).</summary>
    public const int ServerLimit = 128;

    /// <summary>Интерфейсы, которые десинк не трогает: петля и туннели — наш TUN и чужие VPN.</summary>
    private static readonly string[] Tunnels = ["tun+", "ipsec+", "wg+", "tap+"];

    /// <summary>Локальные сети — те же, что у winws2 (<c>DIVERT_NO_LOCALNETSv4/6</c>, <c>nfqws.c</c>).</summary>
    private static readonly string[] Lan4 = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];
    private static readonly string[] Lan6 = ["fc00::/7", "fe80::/10", "ff00::/8", "2001::/32"];

    /// <summary>
    /// Пакет для <c>iptables-restore --noflush</c> (или <c>ip6tables-restore</c>).
    /// </summary>
    /// <param name="dropTtlExpiredIcmp">
    /// Отбрасывать ICMP time-exceeded от своих подделок с малым TTL: на Linux
    /// такая ошибка рвёт сокет, ещё стоящий в SYN_SENT (zapret, <c>ipt.sh</c>,
    /// FILTER_TTL_EXPIRED_ICMP).
    /// </param>
    public static string RestoreBatch(
        AddressFamily family,
        CapturePlan plan,
        FirewallExclusions ex,
        KernelFeatures k,
        ushort queue,
        uint mark,
        bool dropTtlExpiredIcmp = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(k);

        bool v6 = family == AddressFamily.InterNetworkV6;
        bool connbytes = v6 ? k.Connbytes6 : k.Connbytes4;
        bool u32 = v6 ? k.U32_6 : k.U32_4;
        bool icmp = dropTtlExpiredIcmp && (v6 ? k.Connmark6 : k.Connmark4);
        var m = Nfqws2CommandLine.Mark(mark);
        var b = plan.Budget;
        var q = queue.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder();

        void Rule(string line) => text.Append(line).Append('\n');

        Rule("*mangle");
        Rule($":{Out} - [0:0]");
        Rule($":{OutQ} - [0:0]");
        Rule($":{In} - [0:0]");
        Rule($":{InQ} - [0:0]");

        // Свои пакеты (подделки nfqws2 с меткой): метка соединения — чтобы
        // отбросить ICMP о них, — и мимо очереди.
        if (icmp)
            Rule($"-A {Out} -m mark --mark {m}/{m} -j CONNMARK --or-mark {m}");

        Rule($"-A {Out} -m mark --mark {m}/{m} -j RETURN");

        if (plan.FilterLoopback)
            Rule($"-A {Out} -o lo -j RETURN");

        foreach (var tunnel in Tunnels)
            Rule($"-A {Out} -o {tunnel} -j RETURN");

        foreach (var range in plan.TcpOut)
        {
            var r = Range(range);

            if (connbytes)
            {
                Rule($"-A {Out} -p tcp --dport {r} -m connbytes --connbytes 1:{b.TcpOut} --connbytes-dir original --connbytes-mode packets -j {OutQ}");
                Rule($"-A {Out} -p tcp --dport {r} --tcp-flags FIN FIN -j {OutQ}");
                Rule($"-A {Out} -p tcp --dport {r} --tcp-flags RST RST -j {OutQ}");
            }
            else
            {
                // Без connbytes — весь исходящий поток портов, как у WinDivert
                // на Windows; FIN и RST в него уже входят.
                Rule($"-A {Out} -p tcp --dport {r} -j {OutQ}");
            }
        }

        foreach (var range in plan.UdpOut)
        {
            var r = Range(range);

            Rule(connbytes
                ? $"-A {Out} -p udp --dport {r} -m connbytes --connbytes 1:{b.UdpOut} --connbytes-dir original --connbytes-mode packets -j {OutQ}"
                : $"-A {Out} -p udp --dport {r} -j {OutQ}");
        }

        if (u32)
        {
            foreach (var rule in RawRules(plan.RawParts, v6))
                Rule($"-A {Out} {rule} -j {OutQ}");
        }

        foreach (var rule in Exclusions(plan, ex, v6, "-d", outgoing: true))
            Rule($"-A {OutQ} {rule} -j RETURN");

        Rule($"-A {OutQ} -j NFQUEUE --queue-num {q} --queue-bypass");

        if (plan.FilterLoopback)
            Rule($"-A {In} -i lo -j RETURN");

        foreach (var tunnel in Tunnels)
            Rule($"-A {In} -i {tunnel} -j RETURN");

        // Входящие SYN+ACK, FIN и RST портов исходящего перехвата WinDivert
        // ловит сам («automatically intercepts incoming TCP packets with SYN+ACK,
        // FIN, and RST flags») — здесь это надо сказать правилами.
        foreach (var range in plan.TcpOut)
        {
            var r = Range(range);
            Rule($"-A {In} -p tcp --sport {r} --tcp-flags SYN,ACK SYN,ACK -j {InQ}");
            Rule($"-A {In} -p tcp --sport {r} --tcp-flags FIN FIN -j {InQ}");
            Rule($"-A {In} -p tcp --sport {r} --tcp-flags RST RST -j {InQ}");
        }

        // Явный входящий перехват (--wf-tcp-in, --wf-udp-in) — только со счётчиком:
        // весь входящий поток 443-65535 на телефоне шёл бы через однопоточный
        // процесс целиком.
        if (connbytes)
        {
            foreach (var range in plan.TcpIn)
                Rule($"-A {In} -p tcp --sport {Range(range)} -m connbytes --connbytes 1:{b.TcpIn} --connbytes-dir reply --connbytes-mode packets -j {InQ}");

            foreach (var range in plan.UdpIn)
                Rule($"-A {In} -p udp --sport {Range(range)} -m connbytes --connbytes 1:{b.UdpIn} --connbytes-dir reply --connbytes-mode packets -j {InQ}");
        }

        foreach (var rule in Exclusions(plan, ex, v6, "-s", outgoing: false))
            Rule($"-A {InQ} {rule} -j RETURN");

        Rule($"-A {InQ} -j NFQUEUE --queue-num {q} --queue-bypass");

        // Переходы — последними: цепочка готова раньше, чем в неё попадёт пакет.
        Rule($"-A OUTPUT -j {Out}");
        Rule($"-A INPUT -j {In}");
        Rule("COMMIT");

        if (icmp)
        {
            Rule("*filter");
            Rule($":{Icmp} - [0:0]");
            Rule(v6
                ? $"-A {Icmp} -p ipv6-icmp -m icmp6 --icmpv6-type time-exceeded -m connmark --mark {m}/{m} -j DROP"
                : $"-A {Icmp} -p icmp -m icmp --icmp-type time-exceeded -m connmark --mark {m}/{m} -j DROP");

            // Первым в INPUT: цепочки Android могли бы принять ICMP раньше.
            Rule($"-I INPUT 1 -j {Icmp}");
            Rule("COMMIT");
        }

        return text.ToString();
    }

    /// <summary>Исключения цепочек очереди: локальные сети, подменные адреса, локалки, серверы, UDP Riot.</summary>
    private static IEnumerable<string> Exclusions(CapturePlan plan, FirewallExclusions ex, bool v6, string side, bool outgoing)
    {
        if (plan.FilterLan)
        {
            foreach (var lan in v6 ? Lan6 : Lan4)
                yield return $"{side} {lan}";
        }

        if (outgoing)
        {
            // Дополнение Android: широковещание и групповые адреса десинку ни к чему.
            if (!v6)
            {
                yield return $"{side} 224.0.0.0/4";
                yield return $"{side} 255.255.255.255/32";
            }
            else if (!plan.FilterLan)
            {
                yield return $"{side} ff00::/8";
            }
        }

        foreach (var cidr in Family(ex.FakeRange is null ? [] : [ex.FakeRange], v6))
            yield return $"{side} {cidr}";

        foreach (var cidr in Family(ex.Lans, v6))
            yield return $"{side} {cidr}";

        foreach (var server in ex.Servers.Select(Plain).Where(a => (a.AddressFamily == AddressFamily.InterNetworkV6) == v6).Distinct().Take(ServerLimit))
            yield return $"{side} {server}/{(v6 ? 128 : 32)}";

        foreach (var cidr in Family(ex.UdpOff, v6))
            yield return $"-p udp {side} {cidr}";
    }

    /// <summary>Адрес без зоны IPv6 и без IPv4, завёрнутого в IPv6.</summary>
    private static IPAddress Plain(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());

        return address;
    }

    /// <summary>Подсети нужного семейства в виде «сеть/префикс»; негодные строки пропускаются.</summary>
    private static IEnumerable<string> Family(IEnumerable<string> entries, bool v6)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var text = entry.Trim();
            int hash = text.IndexOf('#');

            if (hash >= 0)
                text = text[..hash].Trim();

            if (Cidr.Parse(text) is { } cidr && cidr.V6 == v6 && seen.Add(cidr.ToString()))
                yield return cidr.ToString();
        }
    }

    private static string Range((int First, int Last) range) =>
        range.First == range.Last
            ? range.First.ToString(CultureInfo.InvariantCulture)
            : $"{range.First.ToString(CultureInfo.InvariantCulture)}:{range.Last.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Части фильтра пресетов правилами xt_u32 — из <c>init.d/custom.d.examples.linux</c> zapret2.
    /// </summary>
    /// <remarks>
    /// STUN: длина UDP не меньше 28 (данных ≥ 20), magic cookie <c>0x2112A442</c>,
    /// два старших бита типа нулевые. Discord: размер 0x52 и два из шестнадцати
    /// нулевых слов — «упрощённая проверка» zapret; часть Windows — только IPv4.
    /// WireGuard — три типа рукопожатия по длине и первому слову; на Windows часть
    /// без <c>outbound</c>, здесь в очередь идёт только исходящее — входящее
    /// рукопожатие ни одной секции не нужно. В файле restore выражение без
    /// кавычек: пробелов в нём нет.
    /// </remarks>
    private static IEnumerable<string> RawRules(IReadOnlySet<RawPart> parts, bool v6)
    {
        if (parts.Contains(RawPart.Stun))
        {
            yield return v6
                ? "-p udp -m u32 --u32 44>>16=28:65535&&52=0x2112A442&&48&0xC0000003=0"
                : "-p udp -m u32 --u32 0>>22&0x3C@4>>16=28:65535&&0>>22&0x3C@12=0x2112A442&&0>>22&0x3C@8&0xC0000003=0";
        }

        if (parts.Contains(RawPart.DiscordMedia) && !v6)
        {
            const string Discord = "0>>22&0x3C@4>>16=0x52&&0>>22&0x3C@8=0x00010046&&0>>22&0x3C@16=0&&0>>22&0x3C@76=0";
            yield return $"-p udp --dport 50000:50099 -m u32 --u32 {Discord}";
            yield return $"-p udp --dport 19294:19344 -m u32 --u32 {Discord}";
        }

        if (parts.Contains(RawPart.WireGuard))
        {
            if (v6)
            {
                yield return "-p udp -m u32 --u32 44>>16=156&&48=0x01000000";
                yield return "-p udp -m u32 --u32 44>>16=100&&48=0x02000000";
                yield return "-p udp -m u32 --u32 44>>16=72&&48=0x03000000";
            }
            else
            {
                yield return "-p udp -m u32 --u32 0>>22&0x3C@4>>16=156&&0>>22&0x3C@8=0x01000000";
                yield return "-p udp -m u32 --u32 0>>22&0x3C@4>>16=100&&0>>22&0x3C@8=0x02000000";
                yield return "-p udp -m u32 --u32 0>>22&0x3C@4>>16=72&&0>>22&0x3C@8=0x03000000";
            }
        }
    }

    /// <summary>Чего не умеет ядро и что из-за этого не работает — для окна и журнала.</summary>
    public static IReadOnlyList<string> Warnings(CapturePlan plan, KernelFeatures k)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(k);

        var warnings = new List<string>();

        if (!(k.Mangle4 && k.Nfqueue4))
            warnings.Add("ядро без NFQUEUE в iptables — десинк с root невозможен");

        if (plan.Ipv6 && !(k.Mangle6 && k.Nfqueue6))
            warnings.Add("IPv6 не обрабатывается десинком: у ip6tables нет mangle или NFQUEUE");

        if (!k.Connbytes4)
        {
            warnings.Add("ядро без connbytes: десинк видит все исходящие пакеты перехваченных портов, расход батареи выше");

            if (plan.TcpIn.Count > 0 || plan.UdpIn.Count > 0)
                warnings.Add("ядро без connbytes: входящий перехват пресета (--wf-tcp-in, --wf-udp-in) не ставится");
        }

        if (k.Connbytes4 && plan.Ipv6 && k.Mangle6 && k.Nfqueue6 && !k.Connbytes6)
            warnings.Add("ip6tables без connbytes: по IPv6 десинк видит все исходящие пакеты перехваченных портов");

        if (plan.RawParts.Count > 0 && !k.U32_4)
            warnings.Add("ядро без u32: STUN, Discord и WireGuard на портах вне перехвата десинк не видит");

        if (!k.Connmark4)
            warnings.Add("ядро без CONNMARK: ICMP о подделках с малым TTL не отбрасывается — соединения могут рваться");

        foreach (var dropped in plan.Dropped)
            warnings.Add(dropped);

        foreach (var name in plan.BeyondBudget)
            warnings.Add($"«{name}»: на Android секция получает только первые пакеты соединения");

        return warnings;
    }

    /// <summary>
    /// Снимает все наши правила обоих семейств; повторный вызов безвреден.
    /// </summary>
    /// <remarks>
    /// Сперва переходы из встроенных цепочек, потом наши: <c>-X</c> не удаляет
    /// цепочку, на которую ещё ссылаются, — <c>out</c> раньше <c>outq</c>.
    /// Тем же сценарием пользуется сторож и запуск после сбоя.
    /// </remarks>
    public static string DownScript()
    {
        var text = new StringBuilder();
        text.Append("#!/system/bin/sh\n");
        text.Append("# NetZapret: снять правила перехвата nfqws2 (оба семейства, повтор безвреден).\n");
        text.Append("for T in iptables ip6tables; do\n");
        text.Append("  command -v $T >/dev/null 2>&1 || continue\n");
        text.Append(Down("$T", "  "));
        text.Append("done\n");
        text.Append("exit 0\n");
        return text.ToString();
    }

    /// <summary>Строки снятия для одной программы (<c>iptables</c> или <c>ip6tables</c>).</summary>
    private static string Down(string tool, string indent)
    {
        var text = new StringBuilder();
        text.Append($"{indent}while {tool} -w -t mangle -D OUTPUT -j {Out} 2>/dev/null; do :; done\n");
        text.Append($"{indent}while {tool} -w -t mangle -D INPUT -j {In} 2>/dev/null; do :; done\n");
        text.Append($"{indent}for C in {Out} {OutQ} {In} {InQ} {Probe}; do\n");
        text.Append($"{indent}  {tool} -w -t mangle -F $C 2>/dev/null; {tool} -w -t mangle -X $C 2>/dev/null\n");
        text.Append($"{indent}done\n");
        text.Append($"{indent}while {tool} -w -D INPUT -j {Icmp} 2>/dev/null; do :; done\n");
        text.Append($"{indent}{tool} -w -F {Icmp} 2>/dev/null; {tool} -w -X {Icmp} 2>/dev/null\n");
        return text.ToString();
    }

    /// <summary>
    /// Ставит правила: <c>iptables-restore --noflush</c>, а не принял он — по одному.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Перед ним всегда <see cref="DownScript"/>: <c>--noflush</c> без этого
    /// добавил бы второй переход в цепочку. Пакет restore применяется по таблице
    /// целиком; если отказал — снимаем остатки этого семейства и ставим правила
    /// по одному (цепочки, затем правила по порядку файла, переходы — последними).
    /// </para>
    /// <para>
    /// Без <c>-w</c> у restore (<see cref="KernelFeatures.RestoreWait"/>) замок
    /// xtables может быть занят netd — пять попыток через секунду, как у
    /// magisk-zapret2. Код выхода не ноль — правила не встали.
    /// </para>
    /// </remarks>
    public static string ApplyScript(string? rules4Path, string? rules4, string? rules6Path, string? rules6, bool restoreWait)
    {
        var text = new StringBuilder();
        text.Append("#!/system/bin/sh\n");
        text.Append("# NetZapret: поставить правила перехвата nfqws2.\n");
        text.Append("NZ_RC=0\n");

        if (rules4Path is not null && rules4 is not null)
            Restore(text, "iptables", rules4Path, rules4, restoreWait);

        if (rules6Path is not null && rules6 is not null)
            Restore(text, "ip6tables", rules6Path, rules6, restoreWait);

        text.Append("exit $NZ_RC\n");
        return text.ToString();

        static void Restore(StringBuilder text, string tool, string path, string rules, bool wait)
        {
            var restore = $"{tool}-restore{(wait ? " -w" : string.Empty)} --noflush < {Nfqws2CommandLine.Quote(path)}";

            text.Append("NZ_OK=0\n");
            text.Append("for i in 1 2 3 4 5; do\n");
            text.Append($"  if {restore}; then NZ_OK=1; break; fi\n");
            text.Append(wait ? "  break\n" : "  sleep 1\n");
            text.Append("done\n");
            text.Append("if [ $NZ_OK = 0 ]; then\n");
            text.Append($"  echo 'NZ_FALLBACK {tool}'\n");
            text.Append(Down(tool, "  "));

            foreach (var command in Commands(tool, rules))
                text.Append("  ").Append(command).Append(" || NZ_RC=1\n");

            text.Append("fi\n");
        }
    }

    /// <summary>
    /// Пакет restore — командами по одной, в кавычках для sh.
    /// </summary>
    /// <remarks>
    /// Цепочки создаются первыми, затем правила в порядке файла: переходы
    /// из встроенных цепочек в нём уже последние. Выражения u32 содержат
    /// <c>&amp;</c> и <c>&gt;</c> — каждое слово в одинарных кавычках.
    /// </remarks>
    public static IReadOnlyList<string> Commands(string tool, string batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var commands = new List<string>();
        var rules = new List<string>();
        string table = "filter";

        foreach (var raw in batch.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#') || line == "COMMIT")
                continue;

            if (line.StartsWith('*'))
            {
                table = line[1..];
                continue;
            }

            if (line.StartsWith(':'))
            {
                var chain = line[1..].Split(' ')[0];
                commands.Add($"{tool} -w -t {table} -N {chain}");
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Nfqws2CommandLine.Quote);
            rules.Add($"{tool} -w -t {table} {string.Join(' ', words)}");
        }

        commands.AddRange(rules);
        return commands;
    }

    /// <summary>
    /// Проба netfilter в неиспользуемой цепочке — пакетов она не видит.
    /// </summary>
    /// <remarks>
    /// Вставка правила заставляет ядро проверить расширение (<c>checkentry</c>),
    /// так что отсутствующее сопоставление или цель падает здесь же («No chain/
    /// target/match by that name»). <c>modprobe</c> у Android обычно нет: «не
    /// встроено» и значит «нет». Строки ответа — <c>NZ_FEATURE имя 0|1</c>
    /// (<see cref="ParseProbe"/>).
    /// </remarks>
    public static string ProbeScript(ushort queue, uint mark)
    {
        var q = queue.ToString(CultureInfo.InvariantCulture);
        var m = Nfqws2CommandLine.Mark(mark);
        var text = new StringBuilder();

        text.Append("#!/system/bin/sh\n");
        text.Append("# NetZapret: что умеет netfilter этого телефона.\n");
        text.Append("probe() { T=$1; shift; $T -w -t mangle -A " + Probe + " \"$@\" >/dev/null 2>&1 && echo 1 || echo 0; }\n");
        text.Append("for F in 4 6; do\n");
        text.Append("  T=iptables; [ $F = 6 ] && T=ip6tables\n");
        text.Append("  command -v $T >/dev/null 2>&1 || { echo \"NZ_FEATURE mangle$F 0\"; continue; }\n");
        text.Append($"  $T -w -t mangle -N {Probe} >/dev/null 2>&1 || $T -w -t mangle -F {Probe} >/dev/null 2>&1\n");
        text.Append($"  $T -w -t mangle -L {Probe} -n >/dev/null 2>&1 && echo \"NZ_FEATURE mangle$F 1\" || echo \"NZ_FEATURE mangle$F 0\"\n");
        text.Append($"  echo \"NZ_FEATURE nfqueue$F $(probe $T -p tcp --dport 9 -j NFQUEUE --queue-num {q} --queue-bypass)\"\n");
        text.Append("  echo \"NZ_FEATURE connbytes$F $(probe $T -p tcp --dport 9 -m connbytes --connbytes 1:20 --connbytes-dir original --connbytes-mode packets -j RETURN)\"\n");
        text.Append($"  echo \"NZ_FEATURE mark$F $(probe $T -m mark --mark {m}/{m} -j RETURN)\"\n");
        text.Append($"  echo \"NZ_FEATURE connmark$F $(probe $T -m mark --mark {m}/{m} -j CONNMARK --or-mark {m})\"\n");
        text.Append("  echo \"NZ_FEATURE multiport$F $(probe $T -p tcp -m multiport --dports 80,443 -j RETURN)\"\n");
        text.Append("  if [ $F = 4 ]; then U='0>>22&0x3C@4>>16=28:65535'; else U='44>>16=28:65535'; fi\n");
        text.Append("  echo \"NZ_FEATURE u32_$F $(probe $T -p udp -m u32 --u32 \"$U\" -j RETURN)\"\n");
        text.Append($"  $T -w -t mangle -F {Probe} >/dev/null 2>&1; $T -w -t mangle -X {Probe} >/dev/null 2>&1\n");
        text.Append($"  $T -w -N {Probe} >/dev/null 2>&1\n");
        text.Append("  if [ $F = 4 ]; then I='-p icmp -m icmp --icmp-type time-exceeded'; else I='-p ipv6-icmp -m icmp6 --icmpv6-type time-exceeded'; fi\n");
        text.Append($"  echo \"NZ_FEATURE icmpfilter$F $($T -w -A {Probe} $I -m connmark --mark {m}/{m} -j DROP >/dev/null 2>&1 && echo 1 || echo 0)\"\n");
        text.Append($"  $T -w -F {Probe} >/dev/null 2>&1; $T -w -X {Probe} >/dev/null 2>&1\n");
        text.Append("done\n");
        text.Append("[ -e /proc/net/netfilter/nfnetlink_queue ] && echo \"NZ_FEATURE queuemodule 1\" || echo \"NZ_FEATURE queuemodule 0\"\n");
        text.Append("# пустой COMMIT с --noflush ничего не меняет — только узнаём, принимает ли restore ключ -w\n");
        text.Append("printf '*mangle\\nCOMMIT\\n' | iptables-restore -w --noflush >/dev/null 2>&1 && echo \"NZ_FEATURE restorewait 1\" || echo \"NZ_FEATURE restorewait 0\"\n");
        text.Append("iptables --version 2>&1; uname -r; getenforce 2>/dev/null\n");
        text.Append("grep -q NFQUEUE /proc/net/ip_tables_targets 2>/dev/null && echo \"NZ_INFO nfqueue_loaded 1\"\n");
        text.Append("[ -r /proc/config.gz ] && zcat /proc/config.gz | grep -E 'NFQUEUE|NETLINK_QUEUE|CONNBYTES|MATCH_U32|MULTIPORT|IP6_NF_MANGLE'\n");
        text.Append("exit 0\n");
        return text.ToString();
    }

    /// <summary>
    /// Ответ <see cref="ProbeScript"/> в <see cref="KernelFeatures"/>.
    /// </summary>
    /// <remarks>
    /// Mangle засчитывается вместе с <c>-m mark</c>: первое же правило цепочки —
    /// мимо очереди по своей метке, без него пакет restore не встал бы целиком.
    /// Connmark — вместе с ICMP в filter: одно без другого подделкам не поможет.
    /// </remarks>
    public static KernelFeatures ParseProbe(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var words = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 3 && words[0] == "NZ_FEATURE")
                found[words[1]] = words[2] == "1";
        }

        bool Has(string name) => found.TryGetValue(name, out var value) && value;

        return new KernelFeatures
        {
            Mangle4 = Has("mangle4") && Has("mark4"),
            Mangle6 = Has("mangle6") && Has("mark6"),
            Nfqueue4 = Has("nfqueue4"),
            Nfqueue6 = Has("nfqueue6"),
            Connbytes4 = Has("connbytes4"),
            Connbytes6 = Has("connbytes6"),
            U32_4 = Has("u32_4"),
            U32_6 = Has("u32_6"),
            Connmark4 = Has("connmark4") && Has("icmpfilter4"),
            Connmark6 = Has("connmark6") && Has("icmpfilter6"),
            Multiport = Has("multiport4"),
            RestoreWait = Has("restorewait"),
            QueueModule = Has("queuemodule"),
        };
    }

    /// <summary>
    /// Здоровье раз в 30 с: жив ли процесс, занята ли очередь, стоят ли переходы.
    /// </summary>
    /// <remarks>
    /// Строки: <c>alive</c>; строка очереди из <c>nfnetlink_queue</c> (столбцы
    /// ядра: номер, portid, в очереди, режим, длина копии, отброшено, отброшено
    /// пользователем, номер последнего пакета, 1); <c>rules4</c>/<c>rules6</c>;
    /// <c>/proc/&lt;pid&gt;/stat</c> (utime и stime — 14-й и 15-й, CLK_TCK=100).
    /// Процесс жив, а переходов нет — их снёс файрвол-приложение: поставить заново.
    /// </remarks>
    public static string HealthScript(int pid, ushort queue)
    {
        var p = pid.ToString(CultureInfo.InvariantCulture);
        var q = queue.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder();

        text.Append($"[ -d /proc/{p} ] && echo alive\n");
        text.Append($"grep -E '^ *{q} ' /proc/net/netfilter/nfnetlink_queue 2>/dev/null\n");
        text.Append($"iptables -w -t mangle -C OUTPUT -j {Out} 2>/dev/null && echo rules4\n");
        text.Append($"ip6tables -w -t mangle -C OUTPUT -j {Out} 2>/dev/null && echo rules6\n");
        text.Append($"cat /proc/{p}/stat 2>/dev/null\n");
        return text.ToString();
    }
}
