using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подбор сервера для контрольной пробы <see cref="TunnelReach"/> и честность её вердикта.
/// </summary>
public sealed class TunnelReachTests
{
    private static ProxyServer Server(string tag, string host = "198.51.100.1", ushort port = 443) => new()
    {
        Tag = tag,
        Protocol = ProxyProtocol.Vless,
        Host = host,
        Port = port,
        Credential = "11111111-1111-1111-1111-111111111111",
    };

    private static TunnelReading Reached(string host, int status = 200) => new()
    {
        Host = host,
        Reached = true,
        Status = status,
        Elapsed = TimeSpan.FromMilliseconds(250),
    };

    private static TunnelReading Failed(string host) => new()
    {
        Host = host,
        Reached = false,
        Elapsed = TimeSpan.FromSeconds(8),
        Detail = "нет ответа за 8 с",
    };

    /// <summary>Живой сервер найден в пуле — только тогда можно винить сборку конфига.</summary>
    [Fact]
    public void Exact_active_server_match_attributes_success_to_config()
    {
        var servers = new[]
        {
            Server("🇳🇱 Нидерланды ALT #1", "198.51.100.1"),
            Server("🇳🇱 Нидерланды 10gbit TURBO #3", "198.51.100.3"),
        };

        var target = TunnelReach.PickTarget(servers, "🇳🇱 Нидерланды 10gbit TURBO #3", preferred: null);

        Assert.NotNull(target);
        Assert.True(target!.MatchesActiveServer);
        Assert.Equal("🇳🇱 Нидерланды 10gbit TURBO #3", target.Server.Tag);

        var verdict = TunnelReach.Summarize(target, [Reached("gemini.google.com")]);

        Assert.Equal("Через сервер «🇳🇱 Нидерланды 10gbit TURBO #3»", verdict.Title);
        Assert.Contains("виноват не сервер, а то, как собран конфиг", verdict.Note);
        Assert.Equal(string.Empty, verdict.Cure);
        Assert.False(verdict.IsFailure);
    }

    /// <summary>
    /// Если активный сервер в списке не найден, проба другого сервера не смеет
    /// обвинять конфиг — она честно называет другой сервер и советует переключиться.
    /// </summary>
    [Fact]
    public void Missing_active_server_never_blames_config_on_fallback_server()
    {
        var servers = new[]
        {
            Server("🇳🇱 Нидерланды ALT #1", "198.51.100.1"),
            Server("🇩🇪 Германия #2", "198.51.100.2"),
        };

        var target = TunnelReach.PickTarget(
            servers,
            live: "🇳🇱 Нидерланды 10gbit TURBO #3",
            preferred: null);

        Assert.NotNull(target);
        Assert.False(target!.MatchesActiveServer);
        Assert.Equal("🇳🇱 Нидерланды 10gbit TURBO #3", target.ActiveServer);
        Assert.Equal("🇳🇱 Нидерланды ALT #1", target.Server.Tag);

        var verdict = TunnelReach.Summarize(target, [Reached("gemini.google.com")]);

        Assert.Equal("Через другой сервер «🇳🇱 Нидерланды ALT #1»", verdict.Title);
        Assert.Contains("🇳🇱 Нидерланды 10gbit TURBO #3", verdict.Note);
        Assert.Contains("переключитесь", verdict.Note);
        Assert.DoesNotContain("виноват не сервер", verdict.Note);
        Assert.Contains("🇳🇱 Нидерланды ALT #1", verdict.Cure);
        Assert.False(verdict.IsFailure);
    }

    /// <summary>Служебное имя группы или пустой ответ движка не считаются точным совпадением.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto-latency")]
    [InlineData("direct")]
    public void Unresolved_or_group_name_uses_preferred_and_reports_unverified_active_exit(string? live)
    {
        var servers = new[]
        {
            Server("🇳🇱 Нидерланды ALT #1", "198.51.100.1"),
            Server("🇫🇮 Финляндия", "198.51.100.2"),
        };

        var target = TunnelReach.PickTarget(servers, live, preferred: "🇫🇮 Финляндия");

        Assert.NotNull(target);
        Assert.False(target!.MatchesActiveServer);
        Assert.Null(target.ActiveServer);
        Assert.Equal("🇫🇮 Финляндия", target.Server.Tag);

        var verdict = TunnelReach.Summarize(target, [Reached("a.example"), Failed("b.example")]);

        Assert.Equal("Через другой сервер «🇫🇮 Финляндия»", verdict.Title);
        Assert.Contains("Активный выход туннеля определить не удалось", verdict.Note);
        Assert.DoesNotContain("беда конфига", verdict.Note);
        Assert.Contains("🇫🇮 Финляндия", verdict.Cure);
        Assert.False(verdict.IsFailure);
    }

    /// <summary>Негодный для туннеля сервер (без адреса) в контрольную пробу не берётся.</summary>
    [Fact]
    public void Unusable_outbounds_are_skipped_and_null_returned_when_none_remain()
    {
        var infoBanner = new ProxyServer
        {
            Tag = "Осталось 10 дней",
            Protocol = ProxyProtocol.Vless,
            Host = string.Empty,
            Port = 443,
            Credential = "11111111-1111-1111-1111-111111111111",
        };
        var usable = Server("🇩🇪 Германия");

        var target = TunnelReach.PickTarget([infoBanner, usable], live: "Осталось 10 дней", preferred: null);

        Assert.NotNull(target);
        Assert.False(target!.MatchesActiveServer);
        Assert.Equal("🇩🇪 Германия", target.Server.Tag);

        Assert.Null(TunnelReach.PickTarget([infoBanner], live: "🇩🇪 Германия", preferred: null));
    }
}
