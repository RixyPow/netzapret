using System.Text;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Разбор подписок формата mihomo / Clash Meta (обсуждение №87).
/// </summary>
public sealed class MihomoConfigParserTests
{
    private const string SampleMihomo = """
        mixed-port: 7890
        mode: rule
        proxies:
          - name: "🇩🇪 Германия Reality"
            type: vless
            server: de.example.com
            port: 443
            uuid: 11111111-1111-1111-1111-111111111111
            network: tcp
            tls: true
            udp: true
            flow: xtls-rprx-vision
            servername: www.google.com
            client-fingerprint: chrome
            reality-opts:
              public-key: jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0
              short-id: abcd
          - {name: "🇳🇱 Нидерланды WS", type: vless, server: nl.example.com, port: 443, uuid: 22222222-2222-2222-2222-222222222222, network: ws, tls: true, servername: nl.example.com, alpn: [h2, http/1.1], ws-opts: {path: /ws, headers: {Host: cdn.example.com}}}
          - name: "🇫🇮 Финляндия Trojan"
            type: trojan
            server: fi.example.com
            port: 443
            password: secret-password
            sni: fi.example.com
            skip-cert-verify: true
          - name: "🇺🇸 США SS"
            type: ss
            server: us.example.com
            port: 8388
            cipher: 2022-blake3-aes-128-gcm
            password: c2VjcmV0MTIzNDU2Nzg5MA==
          - name: "🇵🇱 Польша Hysteria2"
            type: hysteria2
            server: pl.example.com
            port: 443
            ports: 20000-50000
            password: hy2-password
            sni: pl.example.com
            obfs: salamander
            obfs-password: obfs-secret
            up: 100 Mbps
            down: 200 Mbps
          - name: "🇯🇵 Япония TUIC"
            type: tuic
            server: jp.example.com
            port: 443
            uuid: 33333333-3333-3333-3333-333333333333
            password: tuic-password
            sni: jp.example.com
            congestion-controller: bbr
            udp-relay-mode: native
          - name: "🇸🇪 Швеция AnyTLS"
            type: anytls
            server: se.example.com
            port: 443
            password: anytls-password
            sni: se.example.com
            client-fingerprint: firefox
          - name: "🇨🇭 Швейцария VMess gRPC"
            type: vmess
            server: ch.example.com
            port: 443
            uuid: 44444444-4444-4444-4444-444444444444
            alterId: 0
            cipher: auto
            tls: true
            network: grpc
            servername: ch.example.com
            grpc-opts:
              grpc-service-name: gun
          - name: "DIRECT"
            type: direct
        proxy-groups:
          - name: PROXY
            type: url-test
            url: https://cp.cloudflare.com/generate_204
            proxies:
              - "🇩🇪 Германия Reality"
        rules:
          - MATCH,PROXY
        """;

    [Fact]
    public void Parses_all_common_mihomo_protocols_and_passes_singbox_check()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(SampleMihomo);

        Assert.Empty(errors);
        Assert.Equal(8, servers.Count);

        var reality = servers[0];
        Assert.Equal(ProxyProtocol.Vless, reality.Protocol);
        Assert.Equal("reality", reality.Security);
        Assert.Equal("xtls-rprx-vision", reality.Flow);
        Assert.Equal("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", reality.RealityPublicKey);
        Assert.Equal("abcd", reality.RealityShortId);

        var ws = servers[1];
        Assert.Equal("ws", ws.Transport);
        Assert.Equal("/ws", ws.Path);
        Assert.Equal("cdn.example.com", ws.HostHeader);
        Assert.Equal(["h2", "http/1.1"], ws.Alpn);

        var hy2 = servers[4];
        Assert.Equal(ProxyProtocol.Hysteria2, hy2.Protocol);
        Assert.Equal("20000:50000", hy2.HopPorts);
        Assert.Equal("salamander", hy2.ObfsType);
        Assert.Equal("obfs-secret", hy2.ObfsPassword);

        var tuic = servers[5];
        Assert.Equal(ProxyProtocol.Tuic, tuic.Protocol);
        Assert.Equal("bbr", tuic.CongestionControl);
        Assert.Equal("native", tuic.UdpRelayMode);

        var anytls = servers[6];
        Assert.Equal(ProxyProtocol.AnyTls, anytls.Protocol);

        var vmess = servers[7];
        Assert.Equal(ProxyProtocol.Vmess, vmess.Protocol);
        Assert.Equal("grpc", vmess.Transport);
        Assert.Equal("gun", vmess.ServiceName);

        Assert.All(servers, s => Assert.True(s.IsUsableOutbound, $"{s.Tag}: {s.Unusable}"));

        // Проверяем сборку в реальном sing-box check.
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions
        {
            HealthInbound = true,
            Keys = EngineKeys.Generate(),
        }).Json;

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, $"sing-box check не пройден: {said}");
    }

    [Fact]
    public void Base64_wrapped_mihomo_and_yaml_anchors_are_parsed()
    {
        const string yaml = """
            x-common: &common
              type: trojan
              port: 443
              password: shared-password
            proxies:
              - <<: *common
                name: "DE"
                server: de.example.com
                sni: de.example.com
            """;

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml));
        var (servers, errors) = SubscriptionParser.ParseBody(b64);

        Assert.Empty(errors);
        var only = Assert.Single(servers);
        Assert.Equal(ProxyProtocol.Trojan, only.Protocol);
        Assert.Equal("DE", only.Tag);
        Assert.Equal("shared-password", only.Credential);
    }

    [Fact]
    public void Mihomo_install_link_is_unwrapped()
    {
        var raw = new Uri("mihomo://install-config?url=https%3A%2F%2Fpanel.example%2Fsub%2Fabc&name=Test");
        Assert.Equal(new Uri("https://panel.example/sub/abc"), SubscriptionClient.Unwrap(raw));
    }
}
