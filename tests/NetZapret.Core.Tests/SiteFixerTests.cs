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
}
