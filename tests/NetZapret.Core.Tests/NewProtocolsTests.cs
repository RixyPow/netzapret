using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// TUIC, Hysteria первой версии, AnyTLS, WireGuard и AmneziaWG — с 01.10.
/// </summary>
/// <remarks>
/// <para>
/// Владелец: «по идее сингбокс всё умеет, можно сделать их поддержку».
/// Движок умел, мы — нет: разбор знал пять схем, остальное отвергал.
/// Ключи здесь выдуманные (случайные 32 байта), адреса — example.com.
/// </para>
/// <para>
/// Главная проверка — последняя: собранный конфиг принимает сам движок.
/// Ему одному здесь можно верить, и именно он 01.10 показал, что выход
/// «parser» с непонятой ссылкой и «jc» строкой роняют конфиг целиком.
/// </para>
/// </remarks>
public sealed class NewProtocolsTests
{
    /// <summary>В ключе «/» — как примерно у каждого второго ключа WireGuard.</summary>
    private const string PrivateKey = "K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=";

    private const string PublicKey = "0e6a0OqdsnKPvxM3rX/zImqcLAMx0J9xPni/hixPltk=";

    private const string Tuic =
        "tuic://11111111-2222-3333-4444-555555555555:pa%24%24@tuic.example.com:443"
        + "?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=tuic.example.com#TUIC";

    private const string Hysteria =
        "hysteria://hy.example.com:8443?protocol=udp&auth=secret&peer=hy.example.com"
        + "&upmbps=30&downmbps=200&obfs=xplus&obfsParam=mask#HY1";

    private const string AnyTls = "anytls://secret@any.example.com:443?sni=any.example.com&fp=chrome#AnyTLS";

    private static readonly string Amnezia =
        "wireguard://" + PrivateKey + "@awg.example.com:51820"
        + "?publickey=" + Uri.EscapeDataString(PublicKey)
        + "&address=10.8.1.2&mtu=1376&jc=4&jmin=40&jmax=70&s1=0&s2=0&h1=100-200&h2=2&h3=3&h4=4#AWG";

    private const string AmneziaConf = """
        [Interface]
        PrivateKey = K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=
        Address = 10.8.1.3/32, fd00::3/128
        DNS = 1.1.1.1
        MTU = 1280
        Jc = 4
        Jmin = 40
        Jmax = 70
        S1 = 15
        S2 = 68
        H1 = 1234567
        H2 = 2345678
        H3 = 3456789
        H4 = 4567890

        [Peer]
        PublicKey = 0e6a0OqdsnKPvxM3rX/zImqcLAMx0J9xPni/hixPltk=
        PresharedKey = K1mwfCsDrWYcuV34G7RXrAiJoatFKtI6/NPLfwCavVQ=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = 203.0.113.7:51820
        PersistentKeepalive = 25
        """;

    private static ProxyServer Parse(string uri)
    {
        Assert.True(ProxyUriParser.TryParse(uri, out var server, out var error), error);
        return server!;
    }

    [Fact]
    public void Tuic_keeps_both_halves_of_its_key()
    {
        var server = Parse(Tuic);

        Assert.Equal(ProxyProtocol.Tuic, server.Protocol);
        Assert.Equal("11111111-2222-3333-4444-555555555555", server.Credential);
        Assert.Equal("pa$$", server.Password);
        Assert.Equal("bbr", server.CongestionControl);
        Assert.Equal("native", server.UdpRelayMode);
        Assert.Equal("TUIC", server.ProtocolName);
    }

    /// <summary>Незнакомое движку слово уронило бы весь конфиг — его не передаём.</summary>
    [Fact]
    public void An_unknown_tuic_congestion_control_is_left_to_the_engine()
    {
        var server = Parse("tuic://u:p@tuic.example.com:443?congestion_control=brutal#X");

        Assert.Null(server.CongestionControl);
    }

    [Fact]
    public void Hysteria_reads_speeds_and_obfuscation()
    {
        var server = Parse(Hysteria);

        Assert.Equal(ProxyProtocol.Hysteria, server.Protocol);
        Assert.Equal("secret", server.Credential);
        Assert.Equal("hy.example.com", server.Sni);
        Assert.Equal(30, server.UpMbps);
        Assert.Equal(200, server.DownMbps);
        Assert.Equal("mask", server.ObfsPassword);
    }

    /// <summary>Без скорости движок не соберёт исходящий: «missing upload speed».</summary>
    [Fact]
    public void Hysteria_without_speeds_gets_cautious_ones()
    {
        var server = Parse("hysteria://hy.example.com:8443?auth=secret#HY1");

        Assert.Equal(10, server.UpMbps);
        Assert.Equal(50, server.DownMbps);
    }

    [Fact]
    public void Hysteria_over_faketcp_is_refused_by_name()
    {
        Assert.False(ProxyUriParser.TryParse("hysteria://hy.example.com:8443?protocol=faketcp#X", out _, out var error));
        Assert.Contains("faketcp", error);
    }

    [Fact]
    public void AnyTls_is_tls_with_a_password()
    {
        var server = Parse(AnyTls);

        Assert.Equal(ProxyProtocol.AnyTls, server.Protocol);
        Assert.Equal("secret", server.Credential);
        Assert.Equal("tls", server.Security);
        Assert.Equal("AnyTLS", server.ProtocolName);
    }

    [Fact]
    public void A_wireguard_key_with_a_slash_survives()
    {
        var server = Parse(Amnezia);

        Assert.Equal(ProxyProtocol.Wireguard, server.Protocol);
        Assert.Equal(PrivateKey, server.Credential);
        Assert.Equal(PublicKey, server.PeerPublicKey);
        Assert.Equal("awg.example.com", server.Host);
        Assert.Equal(51820, server.Port);
        Assert.Equal(["10.8.1.2/32"], server.LocalAddresses);
        Assert.Equal(1376, server.Mtu);
    }

    /// <summary>
    /// Числа числом, заголовок-диапазон строкой — ровно так их принимает движок.
    /// </summary>
    [Fact]
    public void Amnezia_parameters_keep_the_types_the_engine_accepts()
    {
        var server = Parse(Amnezia);

        Assert.Equal("AmneziaWG", server.ProtocolName);
        Assert.Contains("\"jc\":4", server.AmneziaOptions);
        Assert.Contains("\"h1\":\"100-200\"", server.AmneziaOptions);
        Assert.Contains("\"h2\":2", server.AmneziaOptions);
    }

    /// <summary>«"jc": "4"» в конфиге роняет всё — такая ссылка отвергается сама.</summary>
    [Fact]
    public void A_non_numeric_amnezia_number_is_refused()
    {
        Assert.False(ProxyUriParser.TryParse(
            "wg://" + PrivateKey + "@awg.example.com:51820?publickey=x&address=10.0.0.2&jc=four#X",
            out _, out var error));

        Assert.Contains("JC", error);
    }

    [Fact]
    public void A_plain_wireguard_link_has_no_amnezia()
    {
        var server = Parse("wireguard://" + PrivateKey + "@wg.example.com:51820?publickey=x&address=10.0.0.2#WG");

        Assert.Null(server.AmneziaOptions);
        Assert.Equal("WireGuard", server.ProtocolName);
    }

    [Fact]
    public void An_amneziawg_conf_file_is_read()
    {
        Assert.True(WireGuardConf.Looks(AmneziaConf));
        Assert.True(WireGuardConf.TryParse(AmneziaConf, "Мой AmneziaWG", out var server, out var error), error);

        Assert.Equal("Мой AmneziaWG", server!.Tag);
        Assert.Equal("203.0.113.7", server.Host);
        Assert.Equal(51820, server.Port);
        Assert.Equal(PrivateKey, server.Credential);
        Assert.Equal(PublicKey, server.PeerPublicKey);
        Assert.Equal(PrivateKey, server.PreSharedKey);
        Assert.Equal(["10.8.1.3/32", "fd00::3/128"], server.LocalAddresses);
        Assert.Equal(25, server.KeepaliveSeconds);
        Assert.Contains("\"h4\":4567890", server.AmneziaOptions);
    }

    [Fact]
    public void A_conf_without_a_server_says_what_is_missing()
    {
        Assert.False(WireGuardConf.TryParse("[Interface]\nPrivateKey = x\nAddress = 10.0.0.2\n[Peer]\nPublicKey = y\n",
            null, out _, out var error));

        Assert.Contains("Endpoint", error);
    }

    [Fact]
    public void A_sing_box_subscription_with_the_new_protocols_is_read()
    {
        var (servers, errors) = SubscriptionParser.ParseBody("""
            { "outbounds": [
              { "type": "tuic", "tag": "T", "server": "t.example.com", "server_port": 443,
                "uuid": "11111111-2222-3333-4444-555555555555", "password": "p", "congestion_control": "bbr" },
              { "type": "hysteria", "tag": "H", "server": "h.example.com", "server_port": 443,
                "auth_str": "a", "up_mbps": 20, "down_mbps": 100, "obfs": "mask" },
              { "type": "anytls", "tag": "A", "server": "a.example.com", "server_port": 443, "password": "s" }
            ] }
            """);

        Assert.Empty(errors);
        Assert.Equal(
            [ProxyProtocol.Tuic, ProxyProtocol.Hysteria, ProxyProtocol.AnyTls],
            servers.Select(s => s.Protocol));
        Assert.Equal("p", servers[0].Password);
        Assert.Equal("mask", servers[1].ObfsPassword);
        Assert.Equal(20, servers[1].UpMbps);
    }

    [Fact]
    public void Everything_new_passes_sing_box_check()
    {
        Assert.True(WireGuardConf.TryParse(AmneziaConf, "AWG файлом", out var fromConf, out _));

        var servers = new[] { Parse(Tuic), Parse(Hysteria), Parse(AnyTls), Parse(Amnezia), fromConf! };
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        Assert.Equal(5, result.UsedServers.Count);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }
}
