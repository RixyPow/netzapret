using NetZapret.Core.Diagnostics;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Браузер со своим DNS и сертификат НУЦ — разбор по тексту настроек.
/// </summary>
/// <remarks>
/// Сами браузеры и хранилища в тестах не трогаются: проверяется только
/// чтение. Ложная тревога здесь так же вредна, как пропуск: режим Chromium
/// «автоматически» стоит у всех, и жалоба на него стояла бы у каждого.
/// </remarks>
public sealed class BrowserDnsTests
{
    [Fact]
    public void Chromium_secure_mode_is_found_with_its_provider()
    {
        var state = """{"dns_over_https":{"mode":"secure","templates":"https://dns.example/dns-query"}}""";

        Assert.Equal("https://dns.example/dns-query", BrowserDns.ChromiumSecure(state));
    }

    [Theory]
    [InlineData("""{"dns_over_https":{"mode":"automatic"}}""")]
    [InlineData("""{"dns_over_https":{"mode":"off"}}""")]
    [InlineData("""{"browser":{}}""")]
    [InlineData("")]
    [InlineData("не json")]
    public void Automatic_off_and_absent_are_not_a_bypass(string state)
    {
        Assert.Null(BrowserDns.ChromiumSecure(state));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    public void Firefox_trr_on_is_found(string mode)
    {
        var prefs = $"""
            user_pref("network.trr.mode", {mode});
            user_pref("network.trr.uri", "https://mozilla.cloudflare-dns.com/dns-query");
            """;

        Assert.Equal("https://mozilla.cloudflare-dns.com/dns-query", BrowserDns.FirefoxTrr(prefs));
    }

    [Theory]
    [InlineData("""user_pref("network.trr.mode", 5);""")]
    [InlineData("""user_pref("network.trr.mode", 0);""")]
    [InlineData("""user_pref("browser.startup.page", 3);""")]
    public void Firefox_trr_off_is_not_a_bypass(string prefs)
    {
        Assert.Null(BrowserDns.FirefoxTrr(prefs));
    }

    [Theory]
    [InlineData("CN=Russian Trusted Root CA, O=The Ministry of Digital Development and Communications, C=RU", true)]
    [InlineData("CN=Russian Trusted Sub CA, O=The Ministry of Digital Development and Communications, C=RU", true)]
    [InlineData("CN=ISRG Root X1, O=Internet Security Research Group, C=US", false)]
    public void Russian_root_is_recognised_by_name(string subject, bool expected)
    {
        Assert.Equal(expected, RussianRoot.IsRussianRoot(subject));
    }
}
