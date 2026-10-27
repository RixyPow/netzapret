using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Ключ AmneziaVPN vpn:// (обсуждение №40, 27.10).</summary>
public sealed class AmneziaKeyTests
{
    private const string PrivateKey = "K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=";
    private const string ServerKey = "0e6a0OqdsnKPvxM3rX/zImqcLAMx0J9xPni/hixPltk=";

    private const string Conf = $"""
        [Interface]
        Address = 10.8.1.2/32
        DNS = 1.1.1.1, 1.0.0.1
        PrivateKey = {PrivateKey}
        Jc = 4
        Jmin = 10
        Jmax = 50
        S1 = 72
        S2 = 56
        S3 = 32
        S4 = 16
        H1 = 1020325451-1020325500
        H2 = 3288052141
        H3 = 1766607858
        H4 = 2528465083
        I1 = <b 0xc700000001>

        [Peer]
        PublicKey = {ServerKey}
        PresharedKey = {PrivateKey}
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = 203.0.113.7:41820
        PersistentKeepalive = 25
        """;

    /// <summary>Ключ так, как его собирает клиент Amnezia: qCompress и base64url без «=».</summary>
    private static string Key(JsonObject root, bool compress = true)
    {
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        byte[] body = json;

        if (compress)
        {
            using var packed = new MemoryStream();
            packed.Write([(byte)(json.Length >> 24), (byte)(json.Length >> 16), (byte)(json.Length >> 8), (byte)json.Length]);

            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(json);

            body = packed.ToArray();
        }

        return "vpn://" + Convert.ToBase64String(body).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static JsonObject Awg(JsonObject lastConfig, string container = "amnezia-awg", string protocol = "awg") => new()
    {
        ["containers"] = new JsonArray
        {
            new JsonObject
            {
                ["container"] = container,
                [protocol] = new JsonObject
                {
                    ["last_config"] = lastConfig.ToJsonString(),
                    ["port"] = "41820",
                    ["transport_proto"] = "udp",
                },
            },
        },
        ["defaultContainer"] = container,
        ["description"] = "Мой сервер Amnezia",
        ["hostName"] = "203.0.113.7",
    };

    private static ProxyServer Single(string text)
    {
        var found = KeyImport.FromText(text);
        Assert.Null(found.Problem);

        var link = Assert.Single(found.Keys);
        Assert.StartsWith("wireguard://", link);
        Assert.True(ProxyUriParser.TryParse(link, out var server, out var error), error);
        return server!;
    }

    [Fact]
    public void An_awg_key_becomes_the_same_server_as_its_conf()
    {
        var server = Single(Key(Awg(new JsonObject { ["config"] = Conf })));

        Assert.True(WireGuardConf.TryParse(Conf, "Мой сервер Amnezia", out var fromConf, out _));

        Assert.Equal("Мой сервер Amnezia", server.Tag);
        Assert.Equal("AmneziaWG", server.ProtocolName);
        Assert.Equal("203.0.113.7", server.Host);
        Assert.Equal(41820, server.Port);
        Assert.Equal(PrivateKey, server.Credential);
        Assert.Equal(ServerKey, server.PeerPublicKey);
        Assert.Equal(fromConf!.LocalAddresses, server.LocalAddresses);
        Assert.Equal(fromConf.AmneziaOptions, server.AmneziaOptions);
        Assert.Contains("\"s4\":16", server.AmneziaOptions);
        Assert.Contains("\"h1\":\"1020325451-1020325500\"", server.AmneziaOptions);
    }

    /// <summary>Клиент Amnezia читает и несжатый JSON — значит, такие ключи бывают.</summary>
    [Fact]
    public void An_uncompressed_key_is_read_too()
    {
        var server = Single(Key(Awg(new JsonObject { ["config"] = Conf }), compress: false));

        Assert.Equal("AmneziaWG", server.ProtocolName);
    }

    [Fact]
    public void Without_conf_text_the_server_is_built_from_the_key_fields()
    {
        var server = Single(Key(Awg(new JsonObject
        {
            ["client_priv_key"] = PrivateKey,
            ["client_ip"] = "10.8.1.5",
            ["server_pub_key"] = ServerKey,
            ["psk_key"] = PrivateKey,
            ["hostName"] = "203.0.113.7",
            ["port"] = 41820,
            ["mtu"] = "1376",
            ["Jc"] = "3",
            ["H1"] = "100-200",
        })));

        Assert.Equal("203.0.113.7", server.Host);
        Assert.Equal(41820, server.Port);
        Assert.Equal(["10.8.1.5/32"], server.LocalAddresses);
        Assert.Equal(1376, server.Mtu);
        Assert.Equal(PrivateKey, server.PreSharedKey);
        Assert.Contains("\"jc\":3", server.AmneziaOptions);
    }

    [Fact]
    public void A_plain_wireguard_container_has_no_amnezia()
    {
        var plain = Conf.Split('\n').Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), "^(J|S|H|I)[a-z0-9]* =")).ToList();
        var server = Single(Key(Awg(new JsonObject { ["config"] = string.Join('\n', plain) }, "amnezia-wireguard", "wireguard")));

        Assert.Equal("WireGuard", server.ProtocolName);
        Assert.Null(server.AmneziaOptions);
    }

    [Fact]
    public void An_xray_only_key_says_what_is_inside()
    {
        var root = new JsonObject
        {
            ["containers"] = new JsonArray
            {
                new JsonObject
                {
                    ["container"] = "amnezia-xray",
                    ["xray"] = new JsonObject { ["last_config"] = "{\"outbounds\":[]}" },
                },
            },
            ["hostName"] = "203.0.113.7",
        };

        var found = KeyImport.FromText(Key(root));

        Assert.Empty(found.Keys);
        Assert.Contains("XRay", found.Problem);
    }

    [Fact]
    public void A_service_key_without_servers_is_named()
    {
        var found = KeyImport.FromText(Key(new JsonObject { ["api_config"] = new JsonObject() }));

        Assert.Empty(found.Keys);
        Assert.Contains("сервиса Amnezia", found.Problem);
    }

    /// <summary>Ключ — пароль: ни в причине отказа, ни где-то ещё его текста нет.</summary>
    [Fact]
    public void A_broken_key_is_refused_without_echoing_it()
    {
        const string broken = "vpn://секрет-не-base64";

        var found = KeyImport.FromText(broken);

        Assert.Empty(found.Keys);
        Assert.NotNull(found.Problem);
        Assert.DoesNotContain("секрет", found.Problem);
    }

    [Fact]
    public void Ordinary_keys_next_to_an_amnezia_key_are_kept()
    {
        var found = KeyImport.FromText(Key(Awg(new JsonObject { ["config"] = Conf })) + "\ntrojan://secret@a.example:443#Германия");

        Assert.Equal(2, found.Keys.Count);
        Assert.StartsWith("wireguard://", found.Keys[0]);
        Assert.Equal("trojan://secret@a.example:443#Германия", found.Keys[1]);
    }

    /// <summary>Сервер из ключа проходит в конфиг движка тем же путём, что и файл.</summary>
    [Fact]
    public void The_key_server_is_usable_by_the_engine()
    {
        var server = Single(Key(Awg(new JsonObject { ["config"] = Conf })));

        Assert.True(server.IsUsableOutbound);
        Assert.True(server.IsEndpoint);

        // Поля AmneziaWG 2.0 (S3, S4, I1, диапазон в H1) движок обязан принять:
        // прежняя проверка движком шла только с полями первой версии.
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [server], new SingBoxOptions());

        Assert.Single(result.UsedServers);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);

        // Сокет одного семейства, а не пара v4/v6 на карте без IPv6 (№40, 28.10).
        var endpoint = JsonNode.Parse(result.Json)!["endpoints"]!.AsArray().Single()!;
        Assert.Equal("direct", (string?)endpoint["detour"]);
    }

    /// <summary>
    /// Маскировка, которой движок не знает (J1–J3, Itime у AmneziaWG 1.5), не теряется
    /// молча: едет в ссылке и называется при добавлении.
    /// </summary>
    [Fact]
    public void Masking_the_engine_does_not_know_is_kept_and_named()
    {
        var conf = Conf.Replace("Jc = 4", "Jc = 4\nJ1 = <r 2>\nItime = 120", StringComparison.Ordinal);

        var found = KeyImport.FromText(conf, "Новый AWG");

        var link = Assert.Single(found.Keys);
        Assert.Contains("j1=", link);
        Assert.Contains("itime=120", link);
        Assert.Equal("параметров AmneziaWG J1, Itime движок не знает — сервер может не ответить", found.Note);

        // Ссылка разбирается в тот же сервер, и оговорка та же.
        Assert.Equal(found.Note, KeyImport.FromText(link).Note);

        // Движку они не передаются: он бы отверг весь конфиг.
        Assert.True(ProxyUriParser.TryParse(link, out var server, out _));
        Assert.DoesNotContain("j1", server!.AmneziaOptions);
    }

    [Fact]
    public void Known_masking_gives_no_note()
    {
        Assert.Null(KeyImport.FromText(Conf, "AWG").Note);
    }
}
