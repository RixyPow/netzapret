using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Свой домен — одним правилом, кто бы его ни заводил: поиск «Маршрутов»
/// или «Сайт не открывается?» на «Главной» (11.11).
/// </summary>
public sealed class OwnDomainTests
{
    [Theory]
    [InlineData("aternos.org", "*.aternos.org")]
    [InlineData("https://aternos.org/go/", "*.aternos.org")]
    [InlineData("www.example.com", "*.example.com")]
    [InlineData("*.cdn.example.com", "*.cdn.example.com")]
    public void A_site_becomes_its_zone_going_direct(string typed, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}.yaml");

        try
        {
            Assert.Equal(expected, UserRulesFile.AddOwnDomain(typed, path));

            var entry = Assert.Single(UserRulesFile.Load(path).Entries);
            Assert.Equal(MatchKind.Domain, entry.Match);
            Assert.Equal(expected, entry.Value);
            Assert.Equal(RoutingMode.Direct, entry.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>«www» — не повод снять последнюю метку: «www.com» остаётся собой.</summary>
    [Fact]
    public void Www_alone_is_not_stripped_to_a_tld()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}.yaml");

        try
        {
            Assert.Equal("*.www.com", UserRulesFile.AddOwnDomain("www.com", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("дискорд")]
    [InlineData("45.82.160.0/24")]
    public void What_is_not_a_site_writes_nothing(string typed)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}.yaml");

        Assert.Null(UserRulesFile.AddOwnDomain(typed, path));
        Assert.False(File.Exists(path));
    }
}
