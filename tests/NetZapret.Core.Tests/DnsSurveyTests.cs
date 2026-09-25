using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Обзор резолверов: разбор ответов и правило подмены.
/// </summary>
/// <remarks>
/// Сетевую часть здесь не проверить — она проверена живым прогоном 23.09
/// (nz dns): Google, Cloudflare и OpenDNS по UDP отвечали из
/// RIPN-NS5-RU-MSK и подменяли четыре имени из пяти, по DoH и DoT — сами.
/// </remarks>
public sealed class DnsSurveyTests
{
    /// <summary>
    /// Автовыбор (25.09): быстрейший по DoH среди годных в туннель.
    /// Посредник с быстрым ответом и молчащий по DoH не берутся.
    /// </summary>
    [Fact]
    public void TheFastestTunnelResolverByDohIsPicked()
    {
        static DnsSurveyRow Row(DnsProvider provider, double? doh) => new() { Provider = provider, DohMs = doh };

        var slow = new DnsProvider("Медленный", ["1.1.1.1"], "1.1.1.1", "one.one.one.one");
        var fast = new DnsProvider("Быстрый", ["8.8.8.8"], "8.8.8.8", "dns.google");
        var silent = new DnsProvider("Молчит", ["9.9.9.9"], "9.9.9.9", "dns.quad9.net");
        var middleman = new DnsProvider("Посредник", ["45.155.204.190"], "45.155.204.190", "xbox.dns", ForTunnel: false);

        var picked = DnsSurvey.Fastest(
        [
            Row(slow, 80),
            Row(fast, 30),
            Row(silent, null),
            Row(middleman, 5),
        ]);

        Assert.Same(fast, picked);
        Assert.Null(DnsSurvey.Fastest([Row(silent, null), Row(middleman, 5)]));
    }

    /// <summary>Ответ с одной A и сжатым именем, как отдаёт любой резолвер.</summary>
    private static byte[] AnswerA(byte[] query, IPAddress address)
    {
        var reply = query.ToList();
        reply[2] = 0x81;
        reply[3] = 0x80;
        reply[7] = 1;

        // Имя — ссылкой на вопрос (смещение 12), тип A, класс IN, TTL, длина 4.
        reply.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
        reply.AddRange(address.GetAddressBytes());

        return [.. reply];
    }

    [Fact]
    public void A_compressed_answer_is_read()
    {
        var query = DnsWire.Query("example.com", DnsWire.TypeA);
        var answer = DnsWire.Parse(AnswerA(query, IPAddress.Parse("93.184.215.14")));

        Assert.NotNull(answer);
        Assert.Equal(0, answer!.Code);
        Assert.Equal(IPAddress.Parse("93.184.215.14"), Assert.Single(answer.Addresses));
    }

    [Fact]
    public void A_query_is_not_taken_for_an_answer()
    {
        // Без флага ответа это вопрос — например, отражённый назад.
        Assert.Null(DnsWire.Parse(DnsWire.Query("example.com", DnsWire.TypeA)));
    }

    [Fact]
    public void A_truncated_answer_does_not_throw()
    {
        var query = DnsWire.Query("example.com", DnsWire.TypeA);
        var full = AnswerA(query, IPAddress.Parse("1.2.3.4"));

        Assert.NotNull(DnsWire.Parse(full.AsSpan(0, full.Length - 3)));
    }

    [Theory]
    [InlineData("15169 | 8.8.8.0/24 | US | arin | 2000-03-30", "15169")]
    [InlineData("3267 196948 | 193.232.0.0/16 | RU | ripencc |", "3267")]
    [InlineData("garbage", null)]
    public void The_network_number_is_read(string text, string? asn)
    {
        Assert.Equal(asn, DnsSurvey.ParseCymruAsn(text));
    }

    [Theory]
    [InlineData("15169 | US | arin | 2000-03-30 | GOOGLE, US", "GOOGLE")]
    [InlineData("3267 | RU | ripencc | 1994-06-21 | RIPN-NS5-RU-MSK RIPN, RU", "RIPN-NS5-RU-MSK")]
    [InlineData("1 | 2", null)]
    public void The_network_name_is_read(string text, string? name)
    {
        Assert.Equal(name, DnsSurvey.ParseCymruName(text));
    }

    private static IReadOnlyList<IPAddress> Ips(params string[] values) =>
        values.Select(IPAddress.Parse).ToList();

    [Fact]
    public void Overlap_with_the_honest_answer_is_not_spoofing()
    {
        Assert.False(DnsSurvey.IsSpoofed(Ips("104.21.1.1", "172.67.1.1"), Ips("104.21.1.1"), new HashSet<IPAddress>()));
    }

    [Fact]
    public void A_different_node_of_a_delivery_network_is_not_spoofing()
    {
        // У сетей доставки узлов десятки: одно расхождение — не подмена.
        Assert.False(DnsSurvey.IsSpoofed(Ips("13.107.42.14"), Ips("13.107.42.15"), new HashSet<IPAddress>()));
    }

    [Fact]
    public void One_stub_for_many_names_is_spoofing()
    {
        // Заглушка оператора одна на всех закрытых.
        var stub = IPAddress.Parse("195.208.152.206");

        Assert.True(DnsSurvey.IsSpoofed([stub], Ips("104.21.1.1"), new HashSet<IPAddress> { stub }));
    }

    [Fact]
    public void An_empty_or_loopback_answer_is_spoofing()
    {
        Assert.True(DnsSurvey.IsSpoofed([], Ips("104.21.1.1"), new HashSet<IPAddress>()));
        Assert.True(DnsSurvey.IsSpoofed(Ips("127.0.0.1"), Ips("104.21.1.1"), new HashSet<IPAddress>()));
    }

    /// <summary>
    /// Каждый провайдер из списка выбора собирается в конфиг, который
    /// принимает сам sing-box.
    /// </summary>
    /// <remarks>
    /// Апстрим DNS с незнакомым полем движок отвергает целиком, вместе
    /// с туннелем, — так уже было со store_selected. Проверять тут можно
    /// только самим движком.
    /// </remarks>
    [Fact]
    public void Every_choosable_provider_passes_sing_box_check()
    {
        string? singBox = null;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && singBox is null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, "tools");

            if (Directory.Exists(tools))
                singBox = Directory.EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
        }

        if (singBox is null)
            return;

        var engine = NetZapret.Core.Rules.RuleSetLoader.Load("mode: selective\nrules: []\n");

        foreach (var provider in DnsSurvey.Providers.Where(p => p.Choosable))
        {
            var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [], new SingBoxOptions
            {
                DnsServer = provider.TlsAddress!,
                DnsServerName = provider.TlsName,
                DnsServerPath = provider.DohPath,
            }).Json;

            var path = Path.Combine(Path.GetTempPath(), $"netzapret-dns-{Guid.NewGuid():N}.json");

            try
            {
                SingBoxConfigCompiler.WriteToFile(path, json);

                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = singBox,
                    ArgumentList = { "check", "-c", path },
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                })!;

                var said = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                Assert.True(process.ExitCode == 0, $"{provider.Name}: {said}");
            }
            finally
            {
                File.Delete(path);
                File.Delete(SingBoxConfigCompiler.StampPathFor(path));
            }
        }
    }

    [Fact]
    public void A_name_only_certificate_gets_its_name_in_the_config()
    {
        var engine = NetZapret.Core.Rules.RuleSetLoader.Load("mode: selective\nrules: []\n");
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [], new SingBoxOptions
        {
            DnsServer = "83.220.169.155",
            DnsServerName = "dns.comss.one",
            DnsServerPath = "/doh/x/",
        }).Json;

        Assert.Contains("\"server_name\": \"dns.comss.one\"", json);
        Assert.Contains("\"path\": \"/doh/x/\"", json);
    }

    [Fact]
    public void Without_an_honest_answer_nothing_is_claimed()
    {
        Assert.False(DnsSurvey.IsSpoofed([], [], new HashSet<IPAddress>()));
    }
}

/// <summary>
/// Системный резолвер префиксом: у link-local IPv6 без номера адаптера.
/// </summary>
/// <remarks>
/// Обсуждение #5, 25.09: «fe80::…%6/128» в route_address — и sing-box
/// отвергал весь конфиг, туннель не поднимался.
/// </remarks>
public sealed class SystemResolversTests
{
    [Theory]
    [InlineData("fe80::f2b4:d2ff:fec8:b5e6%6", "fe80::f2b4:d2ff:fec8:b5e6/128")]
    [InlineData("2001:4860:4860::8888", "2001:4860:4860::8888/128")]
    [InlineData("192.168.1.1", "192.168.1.1/32")]
    public void APrefixCarriesNoZone(string address, string expected) =>
        Assert.Equal(expected, SystemResolvers.ToPrefix(System.Net.IPAddress.Parse(address)));
}
