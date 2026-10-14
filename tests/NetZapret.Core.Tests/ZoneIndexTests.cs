using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Словарь зон отвечает так же, как прежний перебор: первое правило, покрывающее имя (15.10).
/// </summary>
public sealed class ZoneIndexTests
{
    /// <summary>Прежний перебор — эталон.</summary>
    private static int BruteFirst(IReadOnlyList<string[]> rules, string name)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            foreach (var entry in rules[i])
            {
                var zone = entry.TrimStart('*', '.');

                if (string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    [Fact]
    public void TheFirstCoveringRuleWins()
    {
        string[][] rules =
        [
            ["*.discord.com"],
            ["updates.discord.com", "*.ru"],
            ["amdm.ru"],
        ];

        var index = new ZoneIndex(rules);

        Assert.Equal(0, index.First("updates.discord.com"));
        Assert.Equal(0, index.First("DISCORD.COM"));
        Assert.Equal(1, index.First("amdm.ru"));
        Assert.Equal(1, index.First("ru"));
        Assert.Equal(-1, index.First("example.com"));
        Assert.Equal(-1, index.First("notdiscord.com"));
    }

    [Fact]
    public void MatchesTheBruteForceOnRandomRules()
    {
        var random = new Random(15_10);
        string[] labels = ["ru", "com", "net", "amdm", "discord", "cdn", "api", "x", "su", "xn--p1ai"];

        string Name(int depth) => string.Join('.', Enumerable.Range(0, depth).Select(_ => labels[random.Next(labels.Length)]));

        var rules = Enumerable.Range(0, 40)
            .Select(_ => Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => (random.Next(3) == 0 ? "*." : "") + Name(random.Next(1, 4)))
                .ToArray())
            .ToList();

        var index = new ZoneIndex(rules);

        for (int i = 0; i < 2000; i++)
        {
            var name = Name(random.Next(1, 5));

            Assert.Equal(BruteFirst(rules, name), index.First(name));
        }
    }
}
