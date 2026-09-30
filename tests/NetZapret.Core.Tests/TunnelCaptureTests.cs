using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Серверы туннеля вне перехвата winws2 (30.09): без адресов строка запуска
/// та же, с адресами — один ключ в начале и ничего больше.
/// </summary>
public sealed class TunnelCaptureTests
{
    private static readonly IReadOnlyList<IPAddress> Servers =
    [
        IPAddress.Parse("77.1.1.1"),
        IPAddress.Parse("2a01:4f8:c0c:1::1"),
    ];

    private const string Expected =
        "(ip.DstAddr=77.1.1.1 or ip.SrcAddr=77.1.1.1"
        + " or ipv6.DstAddr=2a01:4f8:c0c:1::1 or ipv6.SrcAddr=2a01:4f8:c0c:1::1 ? false : true)";

    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static IEnumerable<ZapretPreset> ShippedPresets()
    {
        if (Repository() is not { } root)
            yield break;

        foreach (var file in Directory.GetFiles(Path.Combine(root, "presets"), "*.txt"))
            yield return new PresetReader().Load(file);
    }

    private static ZapretPreset WithHead(params string[] head)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-preset-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllLines(path, [.. head, "--filter-tcp=443", "--lua-desync=pass"]);
            return new PresetReader().Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutServersTheCommandLineIsUntouched()
    {
        foreach (var preset in ShippedPresets())
        {
            var plain = WinwsCommandLine.Build(preset, gameFilter: true).ToList();
            var empty = WinwsCommandLine.Build(preset, gameFilter: true, tunnelServers: []).ToList();

            Assert.Equal(plain, empty);
            Assert.DoesNotContain(empty, a => a.StartsWith("--wf-raw-filter=", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Один ключ в самом начале; всё остальное — та же строка до последнего ключа.
    /// </summary>
    [Fact]
    public void ServersAddOneKeyAndChangeNothingElse()
    {
        int checkedPresets = 0;

        foreach (var preset in ShippedPresets())
        {
            var plain = WinwsCommandLine.Build(preset, gameFilter: true).ToList();
            var bypass = WinwsCommandLine.Build(preset, gameFilter: true, tunnelServers: Servers).ToList();

            Assert.Equal("--wf-raw-filter=" + Expected, bypass[0]);
            Assert.Equal(plain, bypass.Skip(1));
            Assert.Equal(Servers.Count, TunnelCapture.Applied(bypass, Servers));

            checkedPresets++;
        }

        Assert.True(checkedPresets > 0, "Пресеты из поставки не нашлись — проверять было нечего.");
    }

    /// <summary>
    /// Форма записи — единственная, которую принимает WinDivert (см. <see cref="TunnelCapture"/>):
    /// <c>!(…)</c> не разбирается, <c>!=</c> теряет IPv6.
    /// </summary>
    [Fact]
    public void TheFilterIsWrittenTheOnlyWayWinDivertTakes()
    {
        var filter = TunnelCapture.Filter(Servers);

        Assert.Equal(Expected, filter);
        Assert.DoesNotContain("!", filter);
        Assert.Equal(string.Empty, TunnelCapture.Filter([]));
    }

    /// <summary>
    /// Подменные адреса туннеля (30.09): соединения на них идут в VPN,
    /// и десинк их не перехватывает — диапазоном, в обе стороны.
    /// </summary>
    [Fact]
    public void TheFakeRangeIsExcludedAsARange()
    {
        Assert.Equal(
            "((ip.DstAddr>=198.18.0.0 and ip.DstAddr<=198.19.255.255)"
            + " or (ip.SrcAddr>=198.18.0.0 and ip.SrcAddr<=198.19.255.255)"
            + " or ip.DstAddr=77.1.1.1 or ip.SrcAddr=77.1.1.1 ? false : true)",
            TunnelCapture.Filter([IPAddress.Parse("77.1.1.1")], "198.18.0.0/15"));

        // Серверов нет (обход без адресов) — диапазон выводится всё равно.
        Assert.StartsWith("((ip.DstAddr>=198.18.0.0", TunnelCapture.Filter([], "198.18.0.0/15"));

        // Без диапазона — прежняя строка, знак в знак.
        Assert.Equal(TunnelCapture.Filter(Servers), TunnelCapture.Filter(Servers, null));
        Assert.Equal(TunnelCapture.Filter(Servers), TunnelCapture.Filter(Servers, "не диапазон"));
    }

    [Fact]
    public void RangeBounds()
    {
        Assert.Equal((IPAddress.Parse("198.18.0.0"), IPAddress.Parse("198.19.255.255")), TunnelCapture.Bounds("198.18.0.0/15"));
        Assert.Equal((IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.255.255.255")), TunnelCapture.Bounds("10.1.2.3/8"));
        Assert.Null(TunnelCapture.Bounds("fc00::/18"));
        Assert.Null(TunnelCapture.Bounds("198.18.0.0"));
        Assert.Null(TunnelCapture.Bounds(null));
    }

    [Fact]
    public void TheFakeRangeIsReadFromTheCompiledConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-fake-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, Compile(("NL", "77.1.1.1")).ToJsonString());
            Assert.Equal("198.18.0.0/15", TunnelEndpoints.FakeRange(path));

            File.WriteAllText(path, "{ \"dns\": { \"servers\": [ { \"type\": \"https\" } ] } }");
            Assert.Null(TunnelEndpoints.FakeRange(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(TunnelEndpoints.FakeRange(path));
    }

    /// <summary>Зона у адреса IPv6 в фильтр не попадает.</summary>
    [Fact]
    public void AnIpv6ScopeIsDropped()
    {
        var filter = TunnelCapture.Filter([IPAddress.Parse("fe80::1%12")]);

        Assert.Equal("(ipv6.DstAddr=fe80::1 or ipv6.SrcAddr=fe80::1 ? false : true)", filter);
    }

    /// <summary>Ключ у winws2 один на запуск: свой фильтр пресета сохраняется, наш встаёт через «и».</summary>
    [Fact]
    public void APresetsOwnInlineFilterIsKept()
    {
        var preset = WithHead("--wf-tcp-out=443", "--wf-raw-filter=ip.TTL>1");
        var arguments = WinwsCommandLine.Build(preset, tunnelServers: Servers);

        Assert.Single(arguments, a => a.StartsWith("--wf-raw-filter=", StringComparison.Ordinal));
        Assert.Contains("--wf-raw-filter=(ip.TTL>1) and " + Expected, arguments);
        Assert.Equal(Servers.Count, TunnelCapture.Applied(arguments, Servers));
    }

    /// <summary>Фильтр пресета файлом не трогаем — и честно говорим, что серверы остались в перехвате.</summary>
    [Fact]
    public void APresetsFilterFromAFileIsLeftAlone()
    {
        var preset = WithHead("--wf-tcp-out=443", "--wf-raw-filter=@windivert.filter/own.txt");

        var plain = WinwsCommandLine.Build(preset).ToList();
        var arguments = WinwsCommandLine.Build(preset, tunnelServers: Servers).ToList();

        Assert.Equal(plain, arguments);
        Assert.Equal(0, TunnelCapture.Applied(arguments, Servers));
    }

    [Fact]
    public void AddressesPastTheLimitStayInTheCapture()
    {
        var many = Enumerable.Range(0, TunnelCapture.Limit + 5)
            .Select(i => IPAddress.Parse($"77.1.{1 + i / 250}.{1 + i % 250}"))
            .ToList();

        var arguments = WinwsCommandLine.Build(WithHead("--wf-tcp-out=443"), tunnelServers: many);
        var filter = Assert.Single(arguments, a => a.StartsWith("--wf-raw-filter=", StringComparison.Ordinal));

        Assert.Equal(TunnelCapture.Limit * 2, filter.Split(" or ").Length);
        Assert.Contains($"ip.DstAddr={many[TunnelCapture.Limit - 1]} ", filter);
        Assert.DoesNotContain($"ip.DstAddr={many[TunnelCapture.Limit]} ", filter);
        Assert.Equal(TunnelCapture.Limit, TunnelCapture.Applied(arguments, many));
    }

    /// <summary>Сети Riot из поставки — те, что выводит часть «Игровой UDP».</summary>
    private static IReadOnlyList<string> RiotNetworks() =>
        Repository() is { } root
            ? AddressListReader.Expand([ProgramCapture.RiotNetwork], root, out _)
            : [];

    /// <summary>
    /// Соседние и вложенные сети сливаются: место в фильтре общее с серверами туннеля.
    /// </summary>
    [Fact]
    public void NetworksMergeIntoRanges()
    {
        var ranges = TunnelCapture.Ranges(
        [
            "138.0.14.0/24", "138.0.12.0/23", "138.0.15.0/24",
            "104.160.128.0/19", "104.160.130.0/24",
            "2a04:82c0::/29",
            "не сеть", "10.0.0.0/33",
        ]);

        Assert.Equal(
        [
            (IPAddress.Parse("104.160.128.0"), IPAddress.Parse("104.160.159.255")),
            (IPAddress.Parse("138.0.12.0"), IPAddress.Parse("138.0.15.255")),
            (IPAddress.Parse("2a04:82c0::"), IPAddress.Parse("2a04:82c7:ffff:ffff:ffff:ffff:ffff:ffff")),
        ], ranges);

        Assert.Equal([(IPAddress.Parse("5.5.5.5"), IPAddress.Parse("5.5.5.5"))], TunnelCapture.Ranges(["5.5.5.5"]));

        // Список из поставки: 22 записи, 13 диапазонов (сверено руками 30.09).
        if (RiotNetworks() is { Count: > 0 } riot)
            Assert.Equal(13, TunnelCapture.Ranges(riot).Count);
    }

    /// <summary>
    /// Игровой UDP Riot (30.09): только UDP и только к этим сетям — TCP к ним
    /// и UDP к прочим остаются в перехвате.
    /// </summary>
    [Fact]
    public void UdpToOffNetworksLeavesTheCapture()
    {
        Assert.Equal(
            "((udp and ((ip.DstAddr>=104.160.128.0 and ip.DstAddr<=104.160.159.255)"
            + " or (ipv6.DstAddr>=2a04:82c0:: and ipv6.DstAddr<=2a04:82c7:ffff:ffff:ffff:ffff:ffff:ffff))) ? false : true)",
            TunnelCapture.Filter([], null, ["104.160.128.0/19", "2a04:82c0::/29"]));

        // Вместе с прочим — после подменных адресов, раньше серверов.
        var all = TunnelCapture.Filter([IPAddress.Parse("77.1.1.1")], "198.18.0.0/15", ["104.160.128.0/19"]);

        Assert.StartsWith("((ip.DstAddr>=198.18.0.0", all);
        Assert.Contains("or (udp and ((ip.DstAddr>=104.160.128.0 and ip.DstAddr<=104.160.159.255))) or ip.DstAddr=77.1.1.1 ", all);

        // Без сетей — прежняя строка, знак в знак.
        Assert.Equal(TunnelCapture.Filter(Servers), TunnelCapture.Filter(Servers, null, []));
    }

    /// <summary>Сети без серверов туннеля (десинк без VPN) — ключ всё равно встаёт, и это видно.</summary>
    [Fact]
    public void UdpOffWorksWithoutTheTunnel()
    {
        var arguments = WinwsCommandLine.Build(WithHead("--wf-tcp-out=443"), udpOff: ["104.160.128.0/19"]);

        Assert.Single(arguments, a => a.StartsWith("--wf-raw-filter=((udp and ", StringComparison.Ordinal));
        Assert.True(TunnelCapture.Carries(arguments, [], null, ["104.160.128.0/19"]));
        Assert.False(TunnelCapture.Carries(arguments, [], null, ["185.40.64.0/22"]));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate bool CompileFilter(
        string filter, int layer, byte[] compiled, uint length, out IntPtr error, out uint position);

    /// <summary>
    /// Условие собирает сам WinDivert — тот, что лежит рядом с winws2.
    /// </summary>
    /// <remarks>
    /// Первая запись, <c>!(…)</c>, выглядела верной и прошла бы любую проверку
    /// текста; WinDivert ответил на неё «parse error», и winws2 с ней не поднялся бы.
    /// Библиотеки нет (движки не скачаны) — проверять нечем, тест молчит.
    /// </remarks>
    [Fact]
    public void WinDivertCompilesTheFilter()
    {
        if (Repository() is not { } root || !Environment.Is64BitProcess)
            return;

        var library = Path.Combine(root, "build", "engines", "zapret", "exe", "WinDivert.dll");

        if (!File.Exists(library))
            return;

        var handle = NativeLibrary.Load(library);

        try
        {
            var compile = Marshal.GetDelegateForFunctionPointer<CompileFilter>(
                NativeLibrary.GetExport(handle, "WinDivertHelperCompileFilter"));

            var many = Enumerable.Range(0, TunnelCapture.Limit / 2)
                .SelectMany(i => new[] { IPAddress.Parse($"77.1.1.{1 + i}"), IPAddress.Parse($"2a01:4f8:c0c:{1 + i:x}::1") })
                .ToList();

            foreach (var filter in new[]
            {
                TunnelCapture.Filter(Servers),
                "(ip.TTL>1) and " + TunnelCapture.Filter(many),
                TunnelCapture.Filter(many, "198.18.0.0/15"),
                TunnelCapture.Filter(many, "198.18.0.0/15", RiotNetworks()),
            })
            {
                bool ok = compile(filter, 0, new byte[65536], 65536, out var error, out uint position);

                Assert.True(ok, $"{Marshal.PtrToStringAnsi(error)} @{position}: {filter}");
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static JsonNode Compile(params (string Tag, string Host)[] servers)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");

        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            servers.Select(s => new ProxyServer
            {
                Protocol = ProxyProtocol.Vless,
                Tag = s.Tag,
                Host = s.Host,
                Port = 443,
                Credential = "PLACEHOLDER",
                Transport = "tcp",
                Security = "tls",
                Sni = "example.com",
            }).ToList(),
            new SingBoxOptions { Scope = TunnelScope.ProxyOnly });

        return JsonNode.Parse(result.Json)!;
    }

    /// <summary>Серверы — из конфига, который собрал сам компилятор, а не из выдуманного.</summary>
    [Fact]
    public void ServersAreReadFromTheCompiledConfig()
    {
        var config = Compile(("NL", "77.1.1.1"), ("EE", "ee.example.com"), ("DE", "77.1.1.1"));

        Assert.Equal(["77.1.1.1", "ee.example.com"], TunnelEndpoints.Hosts(config));
    }

    [Fact]
    public void ThePinnedServerComesFirst()
    {
        var config = Compile(("NL", "77.1.1.1"), ("EE", "77.1.1.2"), ("DE", "77.1.1.3"));

        Assert.Equal(["77.1.1.3", "77.1.1.1", "77.1.1.2"], TunnelEndpoints.Hosts(config, "DE"));
    }

    [Fact]
    public void WireGuardPeersCount()
    {
        var config = JsonNode.Parse("""
            { "endpoints": [ { "type": "wireguard", "peers": [ { "address": "162.159.192.1", "port": 2408 } ] } ] }
            """);

        Assert.Equal(["162.159.192.1"], TunnelEndpoints.Hosts(config));
    }

    /// <summary>
    /// WARP остаётся в перехвате десинка: без него соединение с узлом замерзает
    /// у оператора, и WARP не отвечает (замер 01.10, см. TunnelEndpoints.Hosts).
    /// </summary>
    [Fact]
    public void WarpStaysInTheCapture()
    {
        var config = JsonNode.Parse("""
            { "outbounds": [
                { "type": "vless", "tag": "NL", "server": "77.1.1.1", "server_port": 443 },
                { "type": "masque", "tag": "Cloudflare WARP", "use_http2": true } ] }
            """);

        Assert.Equal(["77.1.1.1"], TunnelEndpoints.Hosts(config, "Cloudflare WARP"));
    }

    [Fact]
    public void AddressesAreReadWithoutRepeatsAndLoopback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-endpoints-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "outbounds": [
                    { "type": "direct", "tag": "direct" },
                    { "type": "vless", "tag": "A", "server": "77.1.1.1", "server_port": 443 },
                    { "type": "vless", "tag": "B", "server": "::ffff:77.1.1.1", "server_port": 443 },
                    { "type": "vless", "tag": "C", "server": "127.0.0.1", "server_port": 443 },
                    { "type": "vless", "tag": "D", "server": "2a01:4f8:c0c:1::1", "server_port": 443 }
                  ]
                }
                """);

            Assert.Equal(
                [IPAddress.Parse("2a01:4f8:c0c:1::1"), IPAddress.Parse("77.1.1.1")],
                TunnelEndpoints.Read(path, first: "D"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingConfigGivesNoAddresses()
    {
        Assert.Empty(TunnelEndpoints.Read(Path.Combine(Path.GetTempPath(), $"nz-none-{Guid.NewGuid():N}.json")));
    }
}
