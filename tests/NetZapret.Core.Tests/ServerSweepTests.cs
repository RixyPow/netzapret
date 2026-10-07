using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Замер всех серверов — «Замерить все» и замер при запуске (07.10).
/// </summary>
public sealed class ServerSweepTests
{
    private static ProxyServer Server(string tag, string host, ushort port = 443) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = tag,
        Host = host,
        Port = port,
        Credential = "PLACEHOLDER",
    };

    /// <summary>
    /// Пробник получает серверы волнами, и в волне с одного входа — не больше
    /// двух: у Trust все страны на одном адресе, и пачка проверок разом
    /// закрывала его на минуту (28.09), а две разом он держит (владелец 07.10).
    /// Прежде пробнику отдавалось до восьми разом.
    /// </summary>
    [Fact]
    public void TwoServersPerEntryInEachWave()
    {
        var servers = new[]
        {
            Server("trust-de", "131.123.25.7"),
            Server("trust-nl", "131.123.25.7"),
            Server("trust-fi", "131.123.25.7"),
            Server("trust-ee", "131.123.25.7"),
            Server("trust-it", "131.123.25.7"),
            Server("sw-ee", "ee.example.com"),
        };

        var waves = ServerSweep.Waves(servers);

        Assert.Equal(2, ServerSweep.PerEntry);
        Assert.Equal(3, waves.Count);
        Assert.Equal(["trust-de", "trust-nl", "sw-ee"], waves[0].Select(s => s.Tag));
        Assert.Equal(["trust-fi", "trust-ee"], waves[1].Select(s => s.Tag));
        Assert.Equal(["trust-it"], waves[2].Select(s => s.Tag));
    }

    [Fact]
    public void OneServerPerEntryWhenAsked()
    {
        var servers = new[]
        {
            Server("trust-de", "131.123.25.7"),
            Server("trust-nl", "131.123.25.7"),
            Server("trust-fi", "131.123.25.7"),
            Server("sw-ee", "ee.example.com"),
            Server("sw-fi", "fi.example.com"),
            Server("sw-fi-2", "FI.example.com"),
            Server("other-port", "131.123.25.7", 8443),
        };

        var waves = ServerSweep.Waves(servers, perEntry: 1);

        Assert.Equal(3, waves.Count);

        Assert.All(waves, wave => Assert.Equal(
            wave.Count,
            wave.Select(s => $"{s.Host}:{s.Port}".ToLowerInvariant()).Distinct().Count()));

        // Порядок внутри входа — как в подписке; потеряться не должен никто.
        Assert.Equal(["trust-de", "sw-ee", "sw-fi", "other-port"], waves[0].Select(s => s.Tag));
        Assert.Equal(["trust-nl", "sw-fi-2"], waves[1].Select(s => s.Tag));
        Assert.Equal(["trust-fi"], waves[2].Select(s => s.Tag));
        Assert.Equal(servers.Length, waves.Sum(w => w.Count));

        Assert.Empty(ServerSweep.Waves([]));
    }

    /// <summary>Замер при запуске стоит трафика и запросов к продавцам — по умолчанию выключен.</summary>
    [Fact]
    public void MeasuringOnStartIsOffByDefault()
    {
        Assert.False(new AppSettings().MeasureOnStart);
        Assert.False(AppSettings.Fresh.MeasureOnStart);
    }
}
