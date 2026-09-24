using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Список доменов ищется по множеству зон (24.09), а отвечать обязан так же,
/// как прежние выражения на каждую запись.
/// </summary>
public sealed class HostListMatchTests
{
    private static readonly string[] Entries =
    [
        "discord.media",
        "*.riotgames.com",
        "Example.ORG",
        "cdn*.example.net",
        "a?c.example.io",
        "  spaced.example  ",
    ];

    public static TheoryData<string> Hosts => new()
    {
        "discord.media",
        "russia1234.discord.media",
        "DISCORD.MEDIA",
        "notdiscord.media",
        "discord.media.evil.com",
        "riotgames.com",
        "auth.riotgames.com",
        "a.b.c.riotgames.com",
        "riotgames.com.ru",
        "example.org",
        "www.example.org",
        "cdn1.example.net",
        "img.cdn7.example.net",
        "cdn.example.net",
        "example.net",
        "abc.example.io",
        "abbc.example.io",
        "spaced.example",
        "x.spaced.example",
        "com",
        "",
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void AnswersLikeARegexPerEntry(string host)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = "lists/проба.txt", Mode = RoutingMode.Proxy };
        rule.Compile();
        rule.LoadHostList(Entries);

        bool expected = Entries.Any(e =>
        {
            var trimmed = e.Trim();
            return GlobMatcher.Compile(trimmed.StartsWith("*.", StringComparison.Ordinal) ? trimmed : "*." + trimmed)
                .IsMatch(host);
        });

        bool actual = rule.Matches(new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemotePort = 443,
            Hostname = host,
        }, out _);

        Assert.Equal(expected, actual);
    }
}
