using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>«Сайт не открывается»: разбор того, что вставил человек.</summary>
public sealed class SiteFixerTests
{
    [Theory]
    [InlineData("https://aternos.org/go/", "aternos.org")]
    [InlineData("pikuco.ru/tests/", "pikuco.ru")]
    [InlineData("  randstuff.ru  ", "randstuff.ru")]
    [InlineData("HTTPS://Translate.Google.com", "translate.google.com")]
    [InlineData("http://пример.рф/путь", "xn--e1afmkfd.xn--p1ai")]
    public void Takes_the_host_from_what_people_paste(string input, string host) =>
        Assert.Equal(host, SiteFixer.HostOf(input));

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("не адрес")]
    public void Rejects_what_is_not_a_site(string input) =>
        Assert.Null(SiteFixer.HostOf(input));

    [Fact]
    public void Rule_covers_the_whole_site_without_www()
    {
        Assert.Equal("*.aternos.org", SiteFixer.RuleValue("www.aternos.org"));
        Assert.Equal("*.translate.google.com", SiteFixer.RuleValue("translate.google.com"));
    }

    /// <summary>
    /// Открыл только VPN — итог называет опробованное и не обещает «только»:
    /// рецепты десинка не перебирались (14.10 — modrinth.com открылся рецептом medium).
    /// </summary>
    [Fact]
    public void Vpn_summary_does_not_claim_vpn_is_the_only_way()
    {
        var text = SiteFixer.ViaVpnSummary("modrinth.com",
        [
            new("как есть (напрямую)", false, "DPI по TLS"),
            new("через VPN", true, "ответ 200"),
        ]);

        Assert.StartsWith("modrinth.com не открылся (как есть (напрямую)), а через VPN открывается.", text);
        Assert.Contains("«через VPN» для *.modrinth.com", text);
        Assert.Contains("рецепты десинка я не пробовал", text);
        Assert.DoesNotContain("только через VPN", text);
    }
}
