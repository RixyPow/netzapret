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
    public void ProxyAllModeSendsEverythingToTheSelectorAndSkipsRules()
    {
        var root = CompileToJson("""
            mode: proxy
            rules:
              - match: domain
                value: "example.com"
                mode: direct
            """, Server());

        Assert.Equal("auto", root.GetProperty("route").GetProperty("final").GetString());
        Assert.DoesNotContain(Rules(root), r => r.TryGetProperty("domain", out _));
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
