using NetZapret.Core.Diagnostics;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Узлы из кэша DNS — к проверке блокировок (08.11, медиа Instagram из чата 07.11).</summary>
public sealed class RecentNodesTests
{
    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cdninstagram.com"] = "Instagram и Facebook · Instagram",
        ["instagram.com"] = "Instagram и Facebook · Instagram",
        ["googlevideo.com"] = "YouTube · Видео",
        ["video.example"] = "Пример · Видео",
        ["example"] = "Пример · Сайт",
    };

    [Fact]
    public void Nodes_inside_a_zone_are_picked_under_its_part_and_the_zone_itself_is_not()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "www.instagram.com" };

        var picked = RecentNodes.Pick(
            ["cdninstagram.com", "scontent-hel3-1.cdninstagram.com", "www.instagram.com", "ya.ru", "1.2.3.4",
             "rr1---sn-ug5onuxaxjvh-v8cz.googlevideo.com."],
            Zones, taken);

        Assert.Equal(
            [("scontent-hel3-1.cdninstagram.com", "Instagram и Facebook · Instagram"),
             ("rr1---sn-ug5onuxaxjvh-v8cz.googlevideo.com", "YouTube · Видео")],
            picked);
        Assert.Contains("scontent-hel3-1.cdninstagram.com", taken);
    }

    [Fact]
    public void The_nearest_zone_wins_and_each_part_gets_at_most_two()
    {
        var picked = RecentNodes.Pick(
            ["a.video.example", "b.video.example", "c.video.example", "www.example", "SCONTENT-A.cdninstagram.com", "scontent-a.cdninstagram.com"],
            Zones, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(2, picked.Count(p => p.Label == "Пример · Видео"));
        Assert.Contains(("www.example", "Пример · Сайт"), picked);
        Assert.Single(picked, p => p.Host == "scontent-a.cdninstagram.com");
    }

    [Fact]
    public void Real_nodes_go_before_general_names()
    {
        var picked = RecentNodes.Pick(
            ["scontent.cdninstagram.com", "static.cdninstagram.com", "scontent-hel3-1.cdninstagram.com"],
            Zones, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("scontent-hel3-1.cdninstagram.com", picked[0].Host);
        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void Total_is_capped()
    {
        var many = Enumerable.Range(0, 100).Select(i => $"n{i}.v{i}.example");
        var zones = Enumerable.Range(0, 100).ToDictionary(i => $"v{i}.example", i => $"часть {i}");

        Assert.Equal(24, RecentNodes.Pick(many, zones, new HashSet<string>()).Count);
    }

    [Fact]
    public void Dns_cache_reads_without_rights()
    {
        // На машине владельца и в CI — Windows; пустой кэш тоже ответ.
        Assert.NotNull(RecentNodes.FromDnsCache());
    }
}
