using System.Net;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подмену DNS провайдера видно без эталона: у проверочных имён адрес есть всегда.
/// </summary>
/// <remarks>
/// Вики zapret.moe: резолвер НСДИ на youtube.com, rutracker.org, www.instagram.com
/// отвечает NXDOMAIN (28.08 и 08.10.2026), а в «Десинке» имена спрашивает Windows.
/// </remarks>
public sealed class SystemDnsCheckTests
{
    private static DnsWire.Answer Answer(int code, params string[] addresses) =>
        new(code, addresses.Select(IPAddress.Parse).ToList(), []);

    [Fact]
    public void No_such_name_on_a_name_that_exists_is_forged() =>
        Assert.True(SystemDnsCheck.LooksForged(Answer(3)));

    [Fact]
    public void An_empty_answer_is_forged() =>
        Assert.True(SystemDnsCheck.LooksForged(Answer(0)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("10.10.34.35")]
    public void A_stub_address_is_forged(string address) =>
        Assert.True(SystemDnsCheck.LooksForged(Answer(0, address)));

    [Fact]
    public void Real_addresses_are_honest() =>
        Assert.False(SystemDnsCheck.LooksForged(Answer(0, "142.250.74.46", "173.194.222.100")));

    /// <summary>SERVFAIL и REFUSED даёт и просто сломанный резолвер — не повод звать его цензором.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void A_server_failure_is_not_called_forgery(int code) =>
        Assert.False(SystemDnsCheck.LooksForged(Answer(code)));

    [Fact]
    public void Applies_only_when_windows_resolves_names()
    {
        static AppSettings Desync(DnsRoute via, string? preset = "Universal V12 Lite") =>
            new AppSettings { PresetName = preset, DnsVia = via }.With(new EngineChoice { Desync = true, Tunnel = false });

        Assert.True(SystemDnsCheck.Applies(Desync(DnsRoute.Auto)));
        Assert.True(SystemDnsCheck.Applies(Desync(DnsRoute.Direct)));
        Assert.False(SystemDnsCheck.Applies(Desync(DnsRoute.Tunnel)));
        Assert.False(SystemDnsCheck.Applies(Desync(DnsRoute.Auto, preset: null)));
    }

    [Fact]
    public void The_advice_names_the_resolver_and_the_names()
    {
        var text = SystemDnsCheck.Advice(new(SystemDnsKind.Forged, "192.168.1.1", ["www.youtube.com", "rutracker.org"]));

        Assert.Contains("192.168.1.1", text);
        Assert.Contains("www.youtube.com, rutracker.org", text);
        Assert.Contains("«через туннель»", text);
    }
}
