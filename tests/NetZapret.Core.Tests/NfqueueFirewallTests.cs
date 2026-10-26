using System.Net;
using System.Net.Sockets;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Правила iptables для nfqws2 (<c>nfqws2.md</c> §4): что ставится, то и снимается.
/// </summary>
public sealed class NfqueueFirewallTests
{
    private static readonly CapturePlan V12Lite = new()
    {
        TcpOut = [(80, 80), (443, 65535)],
        UdpOut = [(443, 443), (6568, 6568), (19294, 19344), (50000, 51000)],
        RawParts = new HashSet<RawPart> { RawPart.Stun, RawPart.DiscordMedia, RawPart.WireGuard },
        Budget = new CaptureBudget(22, 20, 10, 10),
    };

    private static readonly FirewallExclusions Exclusions = new()
    {
        Servers = [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::10")],
        FakeRange = "198.18.0.0/15",
        Lans = ["25.0.0.0/8", "26.0.0.0/8", "2620:9b::/96"],
        UdpOff = ["192.0.2.0/24 # Riot"],
    };

    private static readonly KernelFeatures Everything = new()
    {
        Mangle4 = true, Nfqueue4 = true, Connbytes4 = true, U32_4 = true, Connmark4 = true,
        Mangle6 = true, Nfqueue6 = true, Connbytes6 = true, U32_6 = true, Connmark6 = true,
        RestoreWait = true, QueueModule = true,
    };

    /// <summary>Полный пример разбора (§4.8) — строка в строку.</summary>
    [Fact]
    public void V12LiteIpv4IsTheWorkedExample()
    {
        var batch = NfqueueFirewall.RestoreBatch(AddressFamily.InterNetwork, V12Lite, Exclusions, Everything, 1717, 0x40000000);

        const string Expected = """
            *mangle
            :netzapret_out - [0:0]
            :netzapret_outq - [0:0]
            :netzapret_in - [0:0]
            :netzapret_inq - [0:0]
            -A netzapret_out -m mark --mark 0x40000000/0x40000000 -j CONNMARK --or-mark 0x40000000
            -A netzapret_out -m mark --mark 0x40000000/0x40000000 -j RETURN
            -A netzapret_out -o lo -j RETURN
            -A netzapret_out -o tun+ -j RETURN
            -A netzapret_out -o ipsec+ -j RETURN
            -A netzapret_out -o wg+ -j RETURN
            -A netzapret_out -o tap+ -j RETURN
            -A netzapret_out -p tcp --dport 80 -m connbytes --connbytes 1:22 --connbytes-dir original --connbytes-mode packets -j netzapret_outq
            -A netzapret_out -p tcp --dport 80 --tcp-flags FIN FIN -j netzapret_outq
            -A netzapret_out -p tcp --dport 80 --tcp-flags RST RST -j netzapret_outq
            -A netzapret_out -p tcp --dport 443:65535 -m connbytes --connbytes 1:22 --connbytes-dir original --connbytes-mode packets -j netzapret_outq
            -A netzapret_out -p tcp --dport 443:65535 --tcp-flags FIN FIN -j netzapret_outq
            -A netzapret_out -p tcp --dport 443:65535 --tcp-flags RST RST -j netzapret_outq
            -A netzapret_out -p udp --dport 443 -m connbytes --connbytes 1:20 --connbytes-dir original --connbytes-mode packets -j netzapret_outq
            -A netzapret_out -p udp --dport 6568 -m connbytes --connbytes 1:20 --connbytes-dir original --connbytes-mode packets -j netzapret_outq
            -A netzapret_out -p udp --dport 19294:19344 -m connbytes --connbytes 1:20 --connbytes-dir original --connbytes-mode packets -j netzapret_outq
            -A netzapret_out -p udp --dport 50000:51000 -m connbytes --connbytes 1:20 --connbytes-dir original --connbytes-mode packets -j netzapret_outq
            -A netzapret_out -p udp -m u32 --u32 0>>22&0x3C@4>>16=28:65535&&0>>22&0x3C@12=0x2112A442&&0>>22&0x3C@8&0xC0000003=0 -j netzapret_outq
            -A netzapret_out -p udp --dport 50000:50099 -m u32 --u32 0>>22&0x3C@4>>16=0x52&&0>>22&0x3C@8=0x00010046&&0>>22&0x3C@16=0&&0>>22&0x3C@76=0 -j netzapret_outq
            -A netzapret_out -p udp --dport 19294:19344 -m u32 --u32 0>>22&0x3C@4>>16=0x52&&0>>22&0x3C@8=0x00010046&&0>>22&0x3C@16=0&&0>>22&0x3C@76=0 -j netzapret_outq
            -A netzapret_out -p udp -m u32 --u32 0>>22&0x3C@4>>16=156&&0>>22&0x3C@8=0x01000000 -j netzapret_outq
            -A netzapret_out -p udp -m u32 --u32 0>>22&0x3C@4>>16=100&&0>>22&0x3C@8=0x02000000 -j netzapret_outq
            -A netzapret_out -p udp -m u32 --u32 0>>22&0x3C@4>>16=72&&0>>22&0x3C@8=0x03000000 -j netzapret_outq
            -A netzapret_outq -d 10.0.0.0/8 -j RETURN
            -A netzapret_outq -d 172.16.0.0/12 -j RETURN
            -A netzapret_outq -d 192.168.0.0/16 -j RETURN
            -A netzapret_outq -d 169.254.0.0/16 -j RETURN
            -A netzapret_outq -d 224.0.0.0/4 -j RETURN
            -A netzapret_outq -d 255.255.255.255/32 -j RETURN
            -A netzapret_outq -d 198.18.0.0/15 -j RETURN
            -A netzapret_outq -d 25.0.0.0/8 -j RETURN
            -A netzapret_outq -d 26.0.0.0/8 -j RETURN
            -A netzapret_outq -d 203.0.113.10/32 -j RETURN
            -A netzapret_outq -p udp -d 192.0.2.0/24 -j RETURN
            -A netzapret_outq -j NFQUEUE --queue-num 1717 --queue-bypass
            -A netzapret_in -i lo -j RETURN
            -A netzapret_in -i tun+ -j RETURN
            -A netzapret_in -i ipsec+ -j RETURN
            -A netzapret_in -i wg+ -j RETURN
            -A netzapret_in -i tap+ -j RETURN
            -A netzapret_in -p tcp --sport 80 --tcp-flags SYN,ACK SYN,ACK -j netzapret_inq
            -A netzapret_in -p tcp --sport 80 --tcp-flags FIN FIN -j netzapret_inq
            -A netzapret_in -p tcp --sport 80 --tcp-flags RST RST -j netzapret_inq
            -A netzapret_in -p tcp --sport 443:65535 --tcp-flags SYN,ACK SYN,ACK -j netzapret_inq
            -A netzapret_in -p tcp --sport 443:65535 --tcp-flags FIN FIN -j netzapret_inq
            -A netzapret_in -p tcp --sport 443:65535 --tcp-flags RST RST -j netzapret_inq
            -A netzapret_inq -s 10.0.0.0/8 -j RETURN
            -A netzapret_inq -s 172.16.0.0/12 -j RETURN
            -A netzapret_inq -s 192.168.0.0/16 -j RETURN
            -A netzapret_inq -s 169.254.0.0/16 -j RETURN
            -A netzapret_inq -s 198.18.0.0/15 -j RETURN
            -A netzapret_inq -s 25.0.0.0/8 -j RETURN
            -A netzapret_inq -s 26.0.0.0/8 -j RETURN
            -A netzapret_inq -s 203.0.113.10/32 -j RETURN
            -A netzapret_inq -p udp -s 192.0.2.0/24 -j RETURN
            -A netzapret_inq -j NFQUEUE --queue-num 1717 --queue-bypass
            -A OUTPUT -j netzapret_out
            -A INPUT -j netzapret_in
            COMMIT
            *filter
            :netzapret_icmp - [0:0]
            -A netzapret_icmp -p icmp -m icmp --icmp-type time-exceeded -m connmark --mark 0x40000000/0x40000000 -j DROP
            -I INPUT 1 -j netzapret_icmp
            COMMIT

            """;

        Assert.Equal(Expected.ReplaceLineEndings("\n"), batch);
    }

    [Fact]
    public void Ipv6UsesItsOwnNetworksAndHeaderOffsets()
    {
        var batch = NfqueueFirewall.RestoreBatch(AddressFamily.InterNetworkV6, V12Lite, Exclusions, Everything, 1717, 0x40000000);

        Assert.Contains("-A netzapret_outq -d fc00::/7 -j RETURN\n", batch);
        Assert.Contains("-A netzapret_outq -d 2001::/32 -j RETURN\n", batch);
        Assert.Contains("-A netzapret_outq -d 2620:9b::/96 -j RETURN\n", batch);
        Assert.Contains("-A netzapret_outq -d 2001:db8::10/128 -j RETURN\n", batch);
        Assert.Contains("-A netzapret_inq -s 2001:db8::10/128 -j RETURN\n", batch);
        Assert.Contains("-p udp -m u32 --u32 44>>16=28:65535&&52=0x2112A442&&48&0xC0000003=0", batch);
        Assert.Contains("-p ipv6-icmp -m icmp6 --icmpv6-type time-exceeded", batch);

        // Ни одного адреса IPv4 и ни одной части Discord (у Windows она только IPv4).
        Assert.DoesNotContain("203.0.113.10", batch);
        Assert.DoesNotContain("224.0.0.0", batch);
        Assert.DoesNotContain("198.18.0.0", batch);
        Assert.DoesNotContain("192.0.2.0", batch);
        Assert.DoesNotContain("0x52", batch);
    }

    /// <summary>Без connbytes — весь исходящий поток портов; входящий перехват пресета не ставится.</summary>
    [Fact]
    public void WithoutConnbytesTheWholeOutgoingFlowIsQueued()
    {
        var plan = V12Lite with { TcpIn = [(443, 65535)], UdpIn = [(443, 443)] };
        var kernel = Everything with { Connbytes4 = false };
        var batch = NfqueueFirewall.RestoreBatch(AddressFamily.InterNetwork, plan, Exclusions, kernel, 1717, 0x40000000);

        Assert.DoesNotContain("connbytes", batch);
        Assert.Contains("-A netzapret_out -p tcp --dport 443:65535 -j netzapret_outq\n", batch);
        Assert.Contains("-A netzapret_out -p udp --dport 443 -j netzapret_outq\n", batch);
        Assert.DoesNotContain("--dport 443:65535 --tcp-flags FIN", batch);
        Assert.Contains("--sport 443:65535 --tcp-flags SYN,ACK SYN,ACK", batch);
        Assert.DoesNotContain("-p udp --sport", batch);

        Assert.Contains(NfqueueFirewall.Warnings(plan, kernel), w => w.Contains("connbytes", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingKernelPiecesDropTheirRules()
    {
        var batch = NfqueueFirewall.RestoreBatch(
            AddressFamily.InterNetwork, V12Lite, Exclusions, Everything with { U32_4 = false, Connmark4 = false }, 1717, 0x40000000);

        Assert.DoesNotContain("u32", batch);
        Assert.DoesNotContain("CONNMARK", batch);
        Assert.DoesNotContain("*filter", batch);
        Assert.Contains("-A netzapret_out -m mark --mark 0x40000000/0x40000000 -j RETURN\n", batch);
    }

    [Fact]
    public void QueueAndMarkComeFromTheHost()
    {
        var batch = NfqueueFirewall.RestoreBatch(AddressFamily.InterNetwork, V12Lite, Exclusions, Everything, 1720, 0x20000000);

        Assert.Equal(2, batch.Split('\n').Count(l => l.EndsWith("-j NFQUEUE --queue-num 1720 --queue-bypass", StringComparison.Ordinal)));
        Assert.Contains("-m mark --mark 0x20000000/0x20000000 -j RETURN", batch);
        Assert.DoesNotContain("1717", batch);
        Assert.DoesNotContain("0x40000000", batch);
    }

    [Fact]
    public void ServersAreCapped()
    {
        var servers = Enumerable.Range(0, 200).Select(i => IPAddress.Parse($"203.0.{i / 256}.{i % 256}")).ToList();
        var batch = NfqueueFirewall.RestoreBatch(
            AddressFamily.InterNetwork, V12Lite, new FirewallExclusions { Servers = servers }, Everything, 1717, 0x40000000);

        Assert.Equal(NfqueueFirewall.ServerLimit, batch.Split('\n').Count(l => l.StartsWith("-A netzapret_outq -d 203.0.", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Снятие — зеркало постановки: каждая наша цепочка очищается и удаляется,
    /// каждый переход из встроенной цепочки снимается.
    /// </summary>
    [Fact]
    public void TeardownMirrorsApply()
    {
        var down = NfqueueFirewall.DownScript();

        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var batch = NfqueueFirewall.RestoreBatch(family, V12Lite, Exclusions, Everything, 1717, 0x40000000);
            var lines = batch.Split('\n');

            foreach (var chain in lines.Where(l => l.StartsWith(':')).Select(l => l[1..].Split(' ')[0]))
            {
                Assert.True(
                    down.Contains($"-F {chain}", StringComparison.Ordinal) || down.Contains($" {chain} ", StringComparison.Ordinal) || down.Contains($" {chain};", StringComparison.Ordinal),
                    chain);
                Assert.Contains(chain, down);
            }

            foreach (var jump in lines.Where(l => l.StartsWith("-A OUTPUT ", StringComparison.Ordinal) || l.StartsWith("-A INPUT ", StringComparison.Ordinal)))
            {
                var parts = jump.Split(' ');
                Assert.Contains($"-D {parts[1]} -j {parts[^1]}", down);
            }

            foreach (var jump in lines.Where(l => l.StartsWith("-I INPUT 1 ", StringComparison.Ordinal)))
                Assert.Contains($"-D INPUT -j {jump.Split(' ')[^1]}", down);
        }

        Assert.Contains("for T in iptables ip6tables; do", down);
        Assert.Contains($"-X $C", down);
        Assert.Contains($"for C in {NfqueueFirewall.Out} {NfqueueFirewall.OutQ} {NfqueueFirewall.In} {NfqueueFirewall.InQ} {NfqueueFirewall.Probe}; do", down);
    }

    [Fact]
    public void FallbackCommandsCreateChainsFirstAndQuoteEverything()
    {
        var batch = NfqueueFirewall.RestoreBatch(AddressFamily.InterNetwork, V12Lite, Exclusions, Everything, 1717, 0x40000000);
        var commands = NfqueueFirewall.Commands("iptables", batch);

        Assert.Equal(
            [
                "iptables -w -t mangle -N netzapret_out",
                "iptables -w -t mangle -N netzapret_outq",
                "iptables -w -t mangle -N netzapret_in",
                "iptables -w -t mangle -N netzapret_inq",
                "iptables -w -t filter -N netzapret_icmp",
            ],
            commands.Take(5));

        Assert.Contains("iptables -w -t mangle '-A' 'OUTPUT' '-j' 'netzapret_out'", commands);
        Assert.Contains("iptables -w -t filter '-I' 'INPUT' '1' '-j' 'netzapret_icmp'", commands);
        Assert.Contains(commands, c => c.Contains("'0>>22&0x3C@4>>16=156&&0>>22&0x3C@8=0x01000000'", StringComparison.Ordinal));

        // Переходы из встроенных цепочек — после всех правил своих цепочек той же таблицы.
        foreach (var (table, jump) in new[] { ("mangle", "'-A' 'OUTPUT'"), ("filter", "'-I' 'INPUT'") })
        {
            var own = commands.Where(c => c.Contains($"-t {table} ", StringComparison.Ordinal)).ToList();
            int lastOwn = own.FindLastIndex(c => c.Contains("'-A' 'netzapret_", StringComparison.Ordinal));
            int firstJump = own.FindIndex(c => c.Contains(jump, StringComparison.Ordinal));

            Assert.True(lastOwn >= 0 && lastOwn < firstJump, table);
        }
    }

    [Fact]
    public void ApplyScriptRestoresAndFallsBack()
    {
        var script = NfqueueFirewall.ApplyScript("/r/fw4.rules", "*mangle\nCOMMIT\n", null, null, restoreWait: false);

        Assert.Contains("iptables-restore --noflush < '/r/fw4.rules'", script);
        Assert.Contains("sleep 1", script);
        Assert.Contains("NZ_FALLBACK iptables", script);
        Assert.DoesNotContain("ip6tables", script);
        Assert.EndsWith("exit $NZ_RC\n", script);

        var waiting = NfqueueFirewall.ApplyScript("/r/fw4.rules", "*mangle\nCOMMIT\n", "/r/fw6.rules", "*mangle\nCOMMIT\n", restoreWait: true);
        Assert.Contains("iptables-restore -w --noflush < '/r/fw4.rules'", waiting);
        Assert.Contains("ip6tables-restore -w --noflush < '/r/fw6.rules'", waiting);
    }

    [Fact]
    public void ProbeAnswersBecomeFeatures()
    {
        var script = NfqueueFirewall.ProbeScript(1717, 0x40000000);

        Assert.Contains("--queue-num 1717 --queue-bypass", script);
        Assert.Contains("--mark 0x40000000/0x40000000", script);
        Assert.Contains($"-N {NfqueueFirewall.Probe}", script);

        var features = NfqueueFirewall.ParseProbe(
        [
            "NZ_FEATURE mangle4 1", "NZ_FEATURE nfqueue4 1", "NZ_FEATURE connbytes4 0", "NZ_FEATURE mark4 1",
            "NZ_FEATURE connmark4 1", "NZ_FEATURE multiport4 1", "NZ_FEATURE u32_4 1", "NZ_FEATURE icmpfilter4 0",
            "NZ_FEATURE mangle6 1", "NZ_FEATURE nfqueue6 1", "NZ_FEATURE mark6 0",
            "NZ_FEATURE queuemodule 1", "NZ_FEATURE restorewait 1",
            "iptables v1.8.7 (legacy)", "5.10.198-android12-9",
        ]);

        Assert.Equal(
            new KernelFeatures
            {
                Mangle4 = true, Nfqueue4 = true, Connbytes4 = false, U32_4 = true,
                Connmark4 = false,  // icmp в filter не встал — от CONNMARK одного толку нет
                Mangle6 = false,    // без -m mark первое же правило цепочки не встанет
                Nfqueue6 = true, Multiport = true, QueueModule = true, RestoreWait = true,
            },
            features);
    }

    [Fact]
    public void HealthScriptChecksProcessQueueAndJumps()
    {
        var script = NfqueueFirewall.HealthScript(4321, 1717);

        Assert.Contains("[ -d /proc/4321 ] && echo alive", script);
        Assert.Contains("grep -E '^ *1717 ' /proc/net/netfilter/nfnetlink_queue", script);
        Assert.Contains("iptables -w -t mangle -C OUTPUT -j netzapret_out", script);
        Assert.Contains("ip6tables -w -t mangle -C OUTPUT -j netzapret_out", script);
        Assert.Contains("cat /proc/4321/stat", script);
    }
}
