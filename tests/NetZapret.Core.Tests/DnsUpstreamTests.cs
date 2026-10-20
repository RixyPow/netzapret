using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Резолвер движка из конфига — его прямой путь проверяет DNS «авто» (19.10).
/// </summary>
public sealed class DnsUpstreamTests
{
    [Fact]
    public void TheEngineResolverIsReadFromItsOwnConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-upstream-{Guid.NewGuid():N}.json");

        try
        {
            var json = new SingBoxConfigCompiler().Compile(
                RuleSetLoader.Load("""
                    mode: selective
                    rules:
                      - match: domain
                        value: "*.rutracker.org"
                        mode: proxy
                    """).RuleSet,
                [new ProxyServer { Protocol = ProxyProtocol.Hysteria2, Tag = "NL", Host = "nl.example.com", Port = 4443,
                    Credential = "PLACEHOLDER", Transport = "udp", Security = "tls" }],
                new SingBoxOptions
                {
                    Scope = TunnelScope.ProxyOnly,
                    DnsServer = "83.220.169.155",
                    DnsServerType = "https",
                    DnsServerName = "dns.comss.one",
                    DnsViaAuto = true,
                }).Json;

            SingBoxConfigCompiler.WriteToFile(path, json);

            var upstream = DnsUpstream.FromConfig(path);

            // Ступень TCP в конфиге есть: DoH с адресом и «авто» (20.10).
            Assert.Equal(new DnsUpstream("https", "83.220.169.155", "dns.comss.one", null) { TcpFallback = true }, upstream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Без ступени в конфиге TCP не пробуется вовсе — и сети не касается.</summary>
    [Fact]
    public async Task WithoutATcpStepTcpIsNotTried()
    {
        var upstream = new DnsUpstream("https", "192.0.2.1", "dns.example.net", null);

        Assert.False(upstream.TcpFallback);
        Assert.False(await upstream.AnswersByTcpAsync(CancellationToken.None));
    }

    [Fact]
    public void NoConfigMeansNothingToCheck()
    {
        Assert.Null(DnsUpstream.FromConfig(Path.Combine(Path.GetTempPath(), $"nz-missing-{Guid.NewGuid():N}.json")));
    }

    /// <summary>Тип, которого проверка не знает, — «отвечает»: «авто» тогда остаётся напрямую, как собран конфиг.</summary>
    [Fact]
    public async Task AnUnknownTypeIsTakenAsAnswering()
    {
        Assert.True(await new DnsUpstream("quic", "192.0.2.1", null, null).AnswersDirectlyAsync(CancellationToken.None));
    }
}
