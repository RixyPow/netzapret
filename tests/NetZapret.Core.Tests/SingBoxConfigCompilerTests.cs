using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

public class SingBoxConfigCompilerTests
{
    private static ProxyServer Server(
        string tag = "NL",
        ProxyProtocol protocol = ProxyProtocol.Hysteria2,
        string transport = "udp") => new()
        {
            Protocol = protocol,
            Tag = tag,
            Host = "nl.example.com",
            Port = 4443,
            Credential = "PLACEHOLDER",
            Transport = transport,
            Security = protocol == ProxyProtocol.Hysteria2 ? "tls" : "reality",
            Sni = "nl.example.com",
            RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
            RealityShortId = "abcd",
        };

    private static JsonElement CompileToJson(
        string yaml,
        params ProxyServer[] servers)
    {
        var engine = RuleSetLoader.Load(yaml);
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Rules(JsonElement root) =>
        root.GetProperty("route").GetProperty("rules").EnumerateArray();

    private static JsonElement CompileProxyOnly(string yaml, params string[] resolvers)
    {
        var engine = RuleSetLoader.Load(yaml);
        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [Server()],
            new SingBoxOptions
            {
                Scope = TunnelScope.ProxyOnly,
                DnsServerAddresses = resolvers,
            });

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private const string ProxyOnlyRules = """
        mode: selective
        rules:
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
        """;

    private const string ProxyAllWithRuExclusion = """
        mode: proxy
        rules:
          - match: ip
            value: "5.255.255.0/24"
            mode: direct
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
        """;

    [Fact]
    public void ProxyAllStillSendsRussianAddressesDirect()
    {
        // «Всё через VPN» не должно означать «в том числе туда, где VPN ломает
        // работу»: российские сервисы через зарубежный адрес требуют
        // подтверждений или отказывают вовсе.
        var root = CompileToJson(ProxyAllWithRuExclusion, Server());

        var direct = Rules(root).Single(r =>
            r.TryGetProperty("ip_cidr", out _) &&
            r.TryGetProperty("outbound", out var o) && o.GetString() == "direct");

        Assert.Equal("5.255.255.0/24", direct.GetProperty("ip_cidr")[0].GetString());
    }

    [Fact]
    public void ProxyAllDropsRulesThatOnlyRepeatTheDefault()
    {
        // Правила на прокси в этом режиме избыточны: всё, что не выведено
        // явно, и так уходит в туннель по final. Лишние строки в конфиге
        // потом читаются как «здесь что-то особенное».
        var root = CompileToJson(ProxyAllWithRuExclusion, Server());

        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("domain_suffix", out _));
    }

    [Fact]
    public void StrictProxyKeepsNoExceptionsAtAll()
    {
        // «Без исключений» буквально: даже прямые правила отбрасываются,
        // и российские сервисы уходят в туннель вместе со всем остальным.
        // Это осознанный выбор режима, а не недосмотр.
        var root = CompileToJson("""
            mode: proxy_strict
            rules:
              - match: ip
                value: "5.255.255.0/24"
                mode: direct
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
            """, Server());

        Assert.Equal("auto", root.GetProperty("route").GetProperty("final").GetString());
        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("ip_cidr", out _));
        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("domain_suffix", out _));
    }

    [Fact]
    public void DesyncOnlyLeavesTheTunnelEmpty()
    {
        // Для sing-box этот режим неотличим от выключенного: десинк
        // туннеля не касается вовсе.
        var root = CompileToJson("""
            mode: desync
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
            """, Server());

        Assert.Equal("direct", root.GetProperty("route").GetProperty("final").GetString());
        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("domain_suffix", out _));
    }

    [Fact]
    public void OffModeRoutesNothingThroughTheTunnel()
    {
        var root = CompileToJson("""
            mode: off
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
            """, Server());

        Assert.Equal("direct", root.GetProperty("route").GetProperty("final").GetString());
        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("domain_suffix", out _));
    }

    private static JsonElement CompileWith(string yaml, SingBoxOptions options)
    {
        var engine = RuleSetLoader.Load(yaml);
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Server()], options);

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private static JsonElement DnsServer(JsonElement root, string tag) =>
        root.GetProperty("dns").GetProperty("servers").EnumerateArray()
            .Single(s => s.GetProperty("tag").GetString() == tag);

    [Fact]
    public void APinnedAddressGetsBothCaptureAndARule()
    {
        // Порознь получается хуже, чем ничего: адрес заводится в туннель,
        // правила ему не достаётся, и он уходит в direct переоткрытым сокетом.
        // До VPN не доезжает, а десинк ломается — WinDivert видит уже не поток
        // приложения. Canva давала на этом сотни оборванных соединений.
        var root = CompileWith(ProxyOnlyRules, new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["8.8.8.8/32"],
            PinnedProxyAddresses = ["72.56.93.144/32"],
        });

        Assert.Contains("72.56.93.144/32",
            root.GetProperty("inbounds")[0].GetProperty("route_address")
                .EnumerateArray().Select(e => e.GetString()));

        var rule = Rules(root).Single(r =>
            r.TryGetProperty("ip_cidr", out var c) &&
            c.EnumerateArray().Any(e => e.GetString() == "72.56.93.144/32"));

        Assert.Equal("auto", rule.GetProperty("outbound").GetString());
    }

    [Fact]
    public void APinnedAddressIsRoutedBeforeBroaderRules()
    {
        // Адрес прибит в hosts именно потому, что домен иначе не разрешается.
        // Общее правило по подсети не должно перехватить его первым.
        var root = CompileWith("""
            mode: selective
            rules:
              - match: ip
                value: "72.0.0.0/8"
                mode: direct
              - match: domain
                value: "*.canva.com"
                mode: proxy
            """, new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["8.8.8.8/32"],
            PinnedProxyAddresses = ["72.56.93.144/32"],
        });

        var all = Rules(root).ToArray();

        int pinnedAt = Array.FindIndex(all, r =>
            r.TryGetProperty("ip_cidr", out var c) &&
            c.EnumerateArray().Any(e => e.GetString() == "72.56.93.144/32"));

        int broadAt = Array.FindIndex(all, r =>
            r.TryGetProperty("ip_cidr", out var c) &&
            c.EnumerateArray().Any(e => e.GetString() == "72.0.0.0/8"));

        Assert.True(pinnedAt >= 0 && broadAt >= 0);
        Assert.True(pinnedAt < broadAt, "прибитый адрес должен проверяться раньше общего правила");
    }

    [Fact]
    public void ByDefaultNamesAreResolvedDirectly()
    {
        // Разрешение имён не должно зависеть от туннеля: пока тот не поднялся,
        // иначе не открывается ничего, включая работающее на десинке.
        var root = CompileProxyOnly(ProxyOnlyRules, "8.8.8.8/32");

        Assert.False(DnsServer(root, "remote").TryGetProperty("detour", out _));
        Assert.Equal("remote",
            root.GetProperty("route").GetProperty("default_domain_resolver").GetProperty("server").GetString());
    }

    [Fact]
    public void ThroughTunnelTheResolverIsHiddenFromTheOperator()
    {
        // Ответ на блокировку DoH у операторов: запрос внутри туннеля
        // неотличим от прочего трафика.
        var root = CompileWith(ProxyOnlyRules, new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["8.8.8.8/32"],
            DnsThroughTunnel = true,
        });

        Assert.Equal("auto", DnsServer(root, "remote").GetProperty("detour").GetString());
    }

    [Fact]
    public void ThroughTunnelSubscriptionHostnamesStillResolveOutsideIt()
    {
        // Иначе замкнутый круг: чтобы поднять туннель, нужно разрешить имя
        // сервера, а чтобы разрешить — уже иметь туннель.
        var root = CompileWith(ProxyOnlyRules, new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["8.8.8.8/32"],
            DnsThroughTunnel = true,
        });

        Assert.Equal("bootstrap",
            root.GetProperty("route").GetProperty("default_domain_resolver").GetProperty("server").GetString());
        Assert.Equal("local", DnsServer(root, "bootstrap").GetProperty("type").GetString());
    }

    [Fact]
    public void WithoutServersTheTunnelDetourIsNotUsed()
    {
        // Detour на селектор без единого сервера убил бы разрешение имён
        // целиком — а это состояние наступает при пустой подписке.
        var engine = RuleSetLoader.Load(ProxyOnlyRules);
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [], new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["8.8.8.8/32"],
            DnsThroughTunnel = true,
        });

        var root = JsonDocument.Parse(result.Json).RootElement;

        Assert.False(DnsServer(root, "remote").TryGetProperty("detour", out _));
    }

    [Fact]
    public void TunMtuIsSetAndFitsAnOrdinaryPath()
    {
        // Без явного значения адаптер поднимался с MTU 65535 при физическом
        // интерфейсе в 1500: мелкие запросы проходили, крупные загрузки
        // рассыпались. В Telegram это выглядело как «переписка работает,
        // фотографии не грузятся» — на неисправность маршрутов непохоже вовсе.
        var root = CompileProxyOnly(ProxyOnlyRules, "8.8.8.8/32");
        var mtu = root.GetProperty("inbounds")[0].GetProperty("mtu").GetInt32();

        Assert.InRange(mtu, 1280, 1452);
    }

    [Fact]
    public void TunGetsAnIpV6AddressSoIpV6RoutesAreInstalled()
    {
        // Без адреса IPv6 auto_route не ставит ни одного маршрута IPv6,
        // и резолвер провайдера по адресу fd7d:…::1 остаётся вне туннеля:
        // отвечает настоящими адресами в обход fakeip. Перехват при этом
        // выглядит исправным — обход идёт по второму протоколу.
        var root = CompileProxyOnly(ProxyOnlyRules, "8.8.8.8/32");

        var addresses = root.GetProperty("inbounds")[0].GetProperty("address")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        Assert.Contains(addresses, a => a.Contains(':'));
    }

    [Fact]
    public void DohToSystemResolversIsRejected()
    {
        // Windows 11 сама поднимает запрос до DNS over HTTPS, и тогда он уходит
        // по TCP/443 мимо hijack-dns: домен резолвится по-настоящему, fakeip
        // не выдаётся, и трафик, которому положен туннель, идёт напрямую.
        // Отказ возвращает резолвер на UDP/53, где мы его перехватываем.
        var root = CompileProxyOnly(ProxyOnlyRules, "8.8.8.8/32", "1.1.1.1/32");

        var reject = Rules(root).Single(r =>
            r.TryGetProperty("action", out var a) && a.GetString() == "reject");

        var addresses = reject.GetProperty("ip_cidr").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        Assert.Equal(["8.8.8.8/32", "1.1.1.1/32"], addresses);
        Assert.Equal(443, reject.GetProperty("port")[0].GetInt32());
    }

    [Fact]
    public void DohRejectAppliesOnlyToTunnelTraffic()
    {
        // Наш собственный резолвер — тоже DoH и живёт по тем же адресам.
        // Без привязки к входящему отказ накрыл бы его самого.
        var root = CompileProxyOnly(ProxyOnlyRules, "8.8.8.8/32");

        var reject = Rules(root).Single(r =>
            r.TryGetProperty("action", out var a) && a.GetString() == "reject");

        Assert.Equal("tun-in", reject.GetProperty("inbound")[0].GetString());
    }

    [Fact]
    public void DohRejectStandsBeforeAnyRoutingDecision()
    {
        // Порядок существенный: правило, отправившее запрос в исходящий,
        // сделало бы отказ недостижимым.
        var root = CompileProxyOnly(ProxyOnlyRules, "8.8.8.8/32");
        var rules = Rules(root).ToArray();

        int reject = Array.FindIndex(rules, r =>
            r.TryGetProperty("action", out var a) && a.GetString() == "reject");
        int firstOutbound = Array.FindIndex(rules, r => r.TryGetProperty("outbound", out _));

        Assert.True(reject >= 0, "правило отказа отсутствует");
        Assert.True(reject < firstOutbound, "отказ должен стоять раньше маршрутизации");
    }

    [Fact]
    public void WithoutSelectiveCaptureThereIsNothingToReject()
    {
        // При сплошном перехвате резолверы в туннель не заводятся отдельно,
        // и ломать DoH незачем.
        var root = CompileToJson(ProxyOnlyRules, Server());

        Assert.DoesNotContain(Rules(root), r =>
            r.TryGetProperty("action", out var a) && a.GetString() == "reject");
    }

    [Fact]
    public void ProxyRuleWithAutoServerPointsAtTheSelector()
    {
        var root = CompileToJson("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """, Server());

        var rule = Rules(root).Single(r => r.TryGetProperty("domain_suffix", out _));

        Assert.Equal("rutracker.org", rule.GetProperty("domain_suffix")[0].GetString());
        Assert.Equal("auto", rule.GetProperty("outbound").GetString());
    }

    [Fact]
    public void DesyncRuleCompilesToDirect()
    {
        // Десинк исполняет WinDivert на реальном интерфейсе, поэтому для
        // sing-box такой трафик — обычный direct.
        var root = CompileToJson("""
            mode: selective
            rules:
              - match: domain
                value: "*.youtube.com"
                mode: desync
            """, Server());

        var rule = Rules(root).Single(r => r.TryGetProperty("domain_suffix", out _));
        Assert.Equal("direct", rule.GetProperty("outbound").GetString());
    }

    [Fact]
    public void ProcessRuleUsesPathOrNameDependingOnTheValue()
    {
        var root = CompileToJson("""
            mode: selective
            rules:
              - match: process
                value: "somegame.exe"
                mode: direct
              - match: process
                value: "C:\\Program Files\\App\\app.exe"
                mode: direct
            """, Server());

        Assert.Contains(Rules(root), r => r.TryGetProperty("process_name", out _));
        Assert.Contains(Rules(root), r => r.TryGetProperty("process_path", out _));
    }

    [Fact]
    public void XhttpServersAreSkippedAndReported()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var servers = new[]
        {
            Server("ok"),
            Server("xhttp-node", ProxyProtocol.Vless, transport: "xhttp"),
        };

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        Assert.Single(result.UsedServers);
        Assert.Single(result.SkippedServers);
        Assert.Equal("xhttp-node", result.SkippedServers[0].Tag);
        Assert.DoesNotContain("xhttp", result.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoRoutesThroughLatencyTestNotAFixedFirstServer()
    {
        // Раньше по умолчанию брался первый сервер подписки. У пользователя
        // это оказалась мёртвая нода, и прокси не работал вовсе.
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server("first"), Server("second")], new SingBoxOptions());

        var outbounds = JsonDocument.Parse(result.Json).RootElement
            .GetProperty("outbounds").EnumerateArray().ToList();

        var urltest = outbounds.Single(o => o.GetProperty("type").GetString() == "urltest");
        var selector = outbounds.Single(o => o.GetProperty("type").GetString() == "selector");

        // Автоподбор перебирает все серверы.
        Assert.Equal(2, urltest.GetProperty("outbounds").GetArrayLength());
        Assert.True(urltest.TryGetProperty("interval", out _));

        // Селектор по умолчанию указывает на автоподбор, а не на конкретный сервер.
        Assert.Equal("auto-latency", selector.GetProperty("default").GetString());

        // При этом в селекторе есть и все серверы: только так их можно
        // закрепить вручную через Clash API.
        Assert.Equal(3, selector.GetProperty("outbounds").GetArrayLength());
    }

    [Fact]
    public void LatencyToleranceIsSetToAvoidFlapping()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server()], new SingBoxOptions { LatencyTolerance = 120 });

        var urltest = JsonDocument.Parse(result.Json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "urltest");

        Assert.Equal(120, urltest.GetProperty("tolerance").GetInt32());
    }

    [Fact]
    public void NoSelectorIsEmittedWithoutServers()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, Array.Empty<ProxyServer>(), new SingBoxOptions());

        var types = JsonDocument.Parse(result.Json).RootElement
            .GetProperty("outbounds").EnumerateArray()
            .Select(o => o.GetProperty("type").GetString())
            .ToList();

        Assert.DoesNotContain("urltest", types);
        Assert.DoesNotContain("selector", types);
    }

    [Fact]
    public void DuplicateServerNamesGetUniqueTags()
    {
        // sing-box адресует outbound'ы по тегу, а подписки повторяют названия.
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server("Node"), Server("Node")], new SingBoxOptions());

        var tags = result.ServerTags.Values.ToList();
        Assert.Equal(2, tags.Distinct().Count());
    }

    [Fact]
    public void WithoutServersProxyRulesFallBackToDirect()
    {
        // Ссылка на несуществующий outbound не дала бы конфигу даже запуститься.
        var root = CompileToJson("""
            mode: selective
            rules:
              - match: domain
                value: "example.com"
                mode: proxy
            """);

        var rule = Rules(root).Single(r => r.TryGetProperty("domain", out _));
        Assert.Equal("direct", rule.GetProperty("outbound").GetString());
        Assert.Equal("direct", root.GetProperty("route").GetProperty("final").GetString());
    }

    [Fact]
    public void ProxyAllModeSendsEverythingToTheSelectorButKeepsDirectRules()
    {
        // Прежде этот режим отбрасывал правила целиком, и вместе с ними
        // исключения на прямой проход. Оставлять их обязательно: см.
        // ProxyAllStillSendsRussianAddressesDirect.
        var root = CompileToJson("""
            mode: proxy
            rules:
              - match: domain
                value: "example.com"
                mode: direct
            """, Server());

        Assert.Equal("auto", root.GetProperty("route").GetProperty("final").GetString());

        var kept = Rules(root).Single(r => r.TryGetProperty("domain", out _));

        Assert.Equal("direct", kept.GetProperty("outbound").GetString());
    }

    [Fact]
    public void OffModeProducesDirectOnlyRouting()
    {
        var root = CompileToJson("""
            mode: off
            rules:
              - match: domain
                value: "example.com"
                mode: proxy
            """, Server());

        Assert.Equal("direct", root.GetProperty("route").GetProperty("final").GetString());
        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("domain", out _));
    }

    [Fact]
    public void RealityOutboundCarriesUtlsAndKeys()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server("US", ProxyProtocol.Trojan, "tcp")], new SingBoxOptions());

        var root = JsonDocument.Parse(result.Json).RootElement;
        var outbound = root.GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "trojan");

        var tls = outbound.GetProperty("tls");
        Assert.True(tls.GetProperty("reality").GetProperty("enabled").GetBoolean());
        Assert.Equal("abcd", tls.GetProperty("reality").GetProperty("short_id").GetString());

        // Без подделки отпечатка сервер REALITY не отвечает.
        Assert.True(tls.GetProperty("utls").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void MandatoryFieldsForSingBox112ArePresent()
    {
        var root = CompileToJson("mode: selective\nrules: []", Server());

        // Без default_domain_resolver sing-box 1.12+ отказывается стартовать.
        Assert.True(root.GetProperty("route").TryGetProperty("default_domain_resolver", out _));

        var dnsServers = root.GetProperty("dns").GetProperty("servers").EnumerateArray().ToList();
        Assert.Contains(dnsServers, s => s.GetProperty("type").GetString() == "fakeip");
    }

    [Fact]
    public void ConfigIsWrittenWithoutByteOrderMark()
    {
        // Разборщик JSON в Go спотыкается о BOM.
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-bom-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, """{ "a": 1 }""");

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NonAsciiServerTagsSurviveSerialization()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var result = new SingBoxConfigCompiler()
            .Compile(engine.RuleSet, [Server("🇸🇪 Trojan | Швеция")], new SingBoxOptions());

        Assert.Contains("Швеция", result.Json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сквозная проверка настоящим sing-box. Пропускается, если движка нет:
    /// он не хранится в репозитории.
    /// </summary>
    [Fact]
    public void GeneratedConfigPassesSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: process
                value: "steam.exe"
                mode: direct
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
              - match: domain
                value: "*.youtube.com"
                mode: desync
              - match: ip
                value: "203.0.113.0/24"
                mode: proxy
            """);

        var servers = new[]
        {
            Server("hy2", ProxyProtocol.Hysteria2),
            Server("tj", ProxyProtocol.Trojan, "tcp"),
            Server("vl", ProxyProtocol.Vless, "tcp"),
        };

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-check-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, result.Json);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"sing-box check не пройден: {stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Конфиг пробника обязан оставаться валидным: на нём держится вся проверка
    /// «прокси жив», и его ломала уже одна регрессия — detour DNS-сервера
    /// в пустой direct-исходящий, который sing-box 1.13 отвергает.
    /// </summary>
    [Fact]
    public void ProbeConfigPassesSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var json = new SingBoxConfigCompiler()
            .CompileProbeConfig(Server("probe", ProxyProtocol.Vless, "tcp"), 21099, logPath: null);

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-probe-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, json);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"конфиг пробника не проходит проверку: {stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProbeConfigHasNoTunAndListensOnLoopbackOnly()
    {
        // Пробник обязан работать без прав администратора: никакого TUN,
        // инбаунд только на петле.
        var json = new SingBoxConfigCompiler()
            .CompileProbeConfig(Server(), 21099, logPath: null);

        var root = JsonDocument.Parse(json).RootElement;
        var inbounds = root.GetProperty("inbounds").EnumerateArray().ToList();

        Assert.Single(inbounds);
        Assert.Equal("mixed", inbounds[0].GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", inbounds[0].GetProperty("listen").GetString());
        Assert.DoesNotContain("\"tun\"", json);
    }

    private static string? FindSingBox()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools))
            {
                var found = Directory
                    .EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found is not null)
                    return found;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
