using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Бесплатный WARP: разбор ответа Cloudflare и сборка конфига.
/// </summary>
/// <remarks>
/// Сеть здесь не трогается ни разу. Ответ регистрации взят настоящий
/// по форме, но с выдуманными ключами: в нём закрытый ключ и ключ доступа
/// к записи, и класть такое в репозиторий нельзя.
/// </remarks>
public class WarpTests
{
    private const string PrivateKey = "iPb22D7QbgGiuvn8KzUb9ljzytJUBCV43Q15btye+UI=";
    private const string PeerKey = "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=";

    private static JsonNode Response(string? v6 = "2606:4700:110:8949:fed4:e6b0:e2f7:8ab5") =>
        JsonNode.Parse($$"""
            {
              "id": "t.00000000-0000-0000-0000-000000000000",
              "token": "00000000-0000-0000-0000-000000000000",
              "account": { "id": "a.0000", "account_type": "free", "license": "LICENSE-KEY" },
              "config": {
                "client_id": "kJ4=",
                "peers": [
                  {
                    "public_key": "{{PeerKey}}",
                    "endpoint": {
                      "v4": "162.159.192.1:0",
                      "v6": "[2606:4700:d0::a29f:c001]:0",
                      "host": "engage.cloudflareclient.com:2408"
                    }
                  }
                ],
                "interface": {
                  "addresses": { "v4": "172.16.0.2", "v6": {{(v6 is null ? "null" : $"\"{v6}\"")}} }
                }
              }
            }
            """)!;

    private static ProxyServer WarpServer() =>
        WarpClient.Parse(Response(), PrivateKey, "id", "token").ToServer();

    [Fact]
    public void RegistrationResponseYieldsUsableAccount()
    {
        var account = WarpClient.Parse(Response(), PrivateKey, "acc", "tok");

        Assert.Equal(PrivateKey, account.PrivateKey);
        Assert.Equal(PeerKey, account.PeerPublicKey);
        Assert.Equal("172.16.0.2", account.AddressV4);
        Assert.Equal("2606:4700:110:8949:fed4:e6b0:e2f7:8ab5", account.AddressV6);
        Assert.Equal("kJ4=", account.ClientId);
        Assert.Equal("LICENSE-KEY", account.License);
    }

    /// <summary>
    /// Порт приходит нулём в том поле, где есть адрес, и настоящим — в том,
    /// где вместо адреса имя. Взять только одно поле значило бы получить
    /// либо порт 0, либо имя, закрытое тем же оператором.
    /// </summary>
    [Fact]
    public void EndpointTakesAddressFromOneFieldAndPortFromAnother()
    {
        var account = WarpClient.Parse(Response(), PrivateKey, null, null);

        Assert.Equal("162.159.192.1", account.EndpointHost);
        Assert.Equal(2408, account.EndpointPort);
    }

    [Fact]
    public void AccountWithoutIpV6StillBuildsServer()
    {
        var server = WarpClient.Parse(Response(v6: null), PrivateKey, null, null).ToServer();

        Assert.Equal(["172.16.0.2/32"], server.LocalAddresses);
    }

    [Fact]
    public void ResponseWithoutPeersIsRejected()
    {
        var broken = JsonNode.Parse("""{ "config": { "peers": [] } }""")!;

        Assert.Throws<InvalidOperationException>(() => WarpClient.Parse(broken, PrivateKey, null, null));
    }

    [Fact]
    public void KeyPairIsReadFromEngineOutput()
    {
        var pair = WireGuardKeys.Parse($"PrivateKey: {PrivateKey}\nPublicKey: {PeerKey}\n");

        Assert.NotNull(pair);
        Assert.Equal(PrivateKey, pair!.PrivateKey);
        Assert.Equal(PeerKey, pair.PublicKey);
    }

    [Fact]
    public void EngineComplaintIsNotMistakenForKeys()
    {
        Assert.Null(WireGuardKeys.Parse("FATAL[0000] unknown command \"wg-keypair\""));
    }

    /// <summary>
    /// WireGuard описывается разделом <c>endpoints</c>, а не исходящим:
    /// старая форма убрана из sing-box в 1.13.
    /// </summary>
    [Fact]
    public void WireguardGoesToEndpointsNotOutbounds()
    {
        var root = Compile(WarpServer());

        var endpoint = root.GetProperty("endpoints").EnumerateArray().Single();

        Assert.Equal("wireguard", endpoint.GetProperty("type").GetString());
        Assert.Equal(WarpAccount.DefaultTag, endpoint.GetProperty("tag").GetString());
        Assert.Equal(PrivateKey, endpoint.GetProperty("private_key").GetString());

        var peer = endpoint.GetProperty("peers").EnumerateArray().Single();

        Assert.Equal("162.159.192.1", peer.GetProperty("address").GetString());
        Assert.Equal(2408, peer.GetProperty("port").GetInt32());

        // Поля reserved в 1.14 нет вовсе: движок отвергает конфиг с ним.
        Assert.False(peer.TryGetProperty("reserved", out _));

        Assert.DoesNotContain(
            root.GetProperty("outbounds").EnumerateArray(),
            o => o.GetProperty("tag").GetString() == WarpAccount.DefaultTag);
    }

    /// <summary>
    /// Endpoint обязан входить в группы наравне с исходящими, иначе выбрать
    /// его нечем: селектор знает только своих участников.
    /// </summary>
    [Fact]
    public void EndpointJoinsSelectorAndLatencyGroups()
    {
        var root = Compile(WarpServer());

        foreach (var group in new[] { "auto-latency", "auto" })
        {
            var members = root.GetProperty("outbounds").EnumerateArray()
                .First(o => o.GetProperty("tag").GetString() == group)
                .GetProperty("outbounds").EnumerateArray()
                .Select(m => m.GetString());

            Assert.Contains(WarpAccount.DefaultTag, members);
        }
    }

    /// <summary>
    /// Домен регистрации закрыт по имени в TLS у российских операторов,
    /// а в списках Zapret лежит с рецептом десинка, то есть уходит напрямую.
    /// Без правила запись нельзя завести с той самой машины, которой она нужна.
    /// </summary>
    [Fact]
    public void RegistrationDomainIsRoutedThroughTheTunnel()
    {
        var root = Compile(WarpServer());

        var rule = root.GetProperty("route").GetProperty("rules").EnumerateArray()
            .Where(r => r.TryGetProperty("domain", out _))
            .First(r => r.GetProperty("domain").EnumerateArray()
                .Any(d => d.GetString() == "api.cloudflareclient.com"));

        Assert.Equal("auto", rule.GetProperty("outbound").GetString());
    }

    /// <summary>
    /// При выборочном перехвате в туннель попадает только то, чему подменён
    /// адрес. Без fakeip правило маршрута до регистрации не доживёт: запрос
    /// уйдёт мимо туннеля, не дойдя до разбора правил.
    /// </summary>
    [Fact]
    public void RegistrationDomainGetsFakeIp()
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
            """);

        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [WarpServer()],
            new SingBoxOptions { Scope = TunnelScope.ProxyOnly });

        var suffixes = JsonDocument.Parse(result.Json).RootElement
            .GetProperty("dns").GetProperty("rules").EnumerateArray()
            .First(r => r.GetProperty("server").GetString() == "fake")
            .GetProperty("domain_suffix").EnumerateArray()
            .Select(d => d.GetString());

        Assert.Contains("api.cloudflareclient.com", suffixes);
    }

    /// <summary>
    /// Замер WARP собирал конфиг с исходящим, которого для WireGuard не бывает,
    /// и «Замерить все» объявляло бы его мёртвым, не подключившись ни разу.
    /// </summary>
    [Fact]
    public void ProbeConfigDescribesWireguardAsEndpoint()
    {
        var json = new SingBoxConfigCompiler().CompileProbeConfig(WarpServer(), 21080, null);
        var root = JsonDocument.Parse(json).RootElement;

        var endpoint = root.GetProperty("endpoints").EnumerateArray().Single();

        Assert.Equal("probe-out", endpoint.GetProperty("tag").GetString());
        Assert.Equal("probe-out", root.GetProperty("route").GetProperty("final").GetString());

        // Единственный исходящий — direct, для запросов к резолверу.
        Assert.Equal(
            ["direct"],
            root.GetProperty("outbounds").EnumerateArray()
                .Select(o => o.GetProperty("tag").GetString()));
    }

    /// <summary>
    /// Ссылка WARP не должна уходить в сеть: скачивать нечего, серверы
    /// собираются из учётной записи рядом с настройками.
    /// </summary>
    [Fact]
    public void WarpUrlIsRecognisedAndOthersAreNot()
    {
        Assert.True(SubscriptionClient.IsWarp(new Uri(SubscriptionClient.WarpUrl)));
        Assert.True(SubscriptionClient.IsWarp(new Uri("WARP://free")));
        Assert.False(SubscriptionClient.IsWarp(new Uri("https://example.com/sub")));
        Assert.False(SubscriptionClient.IsWarp(new Uri("happ://add/https://example.com/sub")));
    }

    [Fact]
    public void AccountSurvivesWritingAndReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-warp-{Guid.NewGuid():N}.json");
        var account = WarpClient.Parse(Response(), PrivateKey, "acc", "tok");

        try
        {
            account.Save(path);

            var read = WarpAccount.Load(path);

            Assert.NotNull(read);
            Assert.Equal(account.PrivateKey, read!.PrivateKey);
            Assert.Equal(account.EndpointPort, read.EndpointPort);
            Assert.Equal(account.AddressV6, read.AddressV6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Запись без ключей хуже отсутствующей: конфиг с пустым private_key
    /// движок не примет, и туннель не поднимется вовсе — включая те серверы,
    /// которые к WARP отношения не имеют.
    /// </summary>
    [Fact]
    public void AccountWithoutKeysCountsAsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-warp-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "PrivateKey": "", "PeerPublicKey": "" }""");

            Assert.Null(WarpAccount.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Сквозная проверка настоящим движком. Пропускается, если его нет:
    /// в репозитории он не хранится.
    /// </summary>
    [Fact]
    public void WireguardConfigPassesSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """);

        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [WarpServer()],
            new SingBoxOptions());

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-warp-check-{Guid.NewGuid():N}.json");

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
            File.Delete(SingBoxConfigCompiler.StampPathFor(path));
        }
    }

    /// <summary>Движок обязан уметь выдавать ключи — на этом держится подключение.</summary>
    [Fact]
    public void EngineGeneratesKeyPair()
    {
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var pair = WireGuardKeys.Generate(singBox);

        // 32 байта в base64 — 44 знака с выравниванием.
        Assert.Equal(44, pair.PrivateKey.Length);
        Assert.Equal(44, pair.PublicKey.Length);
        Assert.NotEqual(pair.PrivateKey, pair.PublicKey);
    }

    private static JsonElement Compile(params ProxyServer[] servers)
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
                server: "auto"
            """);

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        return JsonDocument.Parse(result.Json).RootElement.Clone();
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
