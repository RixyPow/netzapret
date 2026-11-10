using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Часть внутри более широкого списка — раньше его правила (29.09, деление Discord).
/// </summary>
public sealed class ServicePartOrderTests
{
    [Fact]
    public void A_narrow_part_is_written_before_its_wider_list()
    {
        // Движок берёт первое совпавшее правило. «Картинки» Discord лежат
        // внутри discordapp.net из «Сайта и переписки»; записанные после,
        // они не сработали бы никогда.
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-rules-{Guid.NewGuid():N}.yaml");

        try
        {
            var file = UserRulesFile.Load(path);
            file.Set(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Desync);
            file.Set(MatchKind.HostList, "config/lists/steam.txt", RoutingMode.Direct);
            file.Set(MatchKind.HostList, "config/lists/discord-images.txt", RoutingMode.Proxy,
                before: "config/lists/discord.txt");

            Assert.Equal(
                ["config/lists/discord-images.txt", "config/lists/discord.txt", "config/lists/steam.txt"],
                file.Entries.Select(e => e.Value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <remarks>
    /// До 01.10 широкий список обязан был быть того же сервиса. «Капчи»
    /// лежат в чужих: hCaptcha — в списке Discord, Turnstile — в Cloudflare,
    /// Arkose — в Roblox. Важно же другое: чтобы широкий список вообще был
    /// в каталоге — иначе «раньше его правила» не найдёт ничего, и часть
    /// встанет в конец, где до неё не дойдёт очередь.
    /// </remarks>
    /// <summary>
    /// «Внутри соседки» (InsideOf) — правда по нашим спискам: соседка той же широкой
    /// части, и каждое имя узкой лежит в её зоне.
    /// </summary>
    [Fact]
    public void Every_inside_of_is_a_sibling_that_holds_all_its_names()
    {
        var parts = ServiceCatalog.All.SelectMany(s => s.Parts).ToList();
        var root = RepoRoot();
        var nested = parts.Where(p => p.InsideOf is not null).ToList();

        Assert.NotEmpty(nested);

        foreach (var part in nested)
        {
            var sibling = Assert.Single(parts, p => p.List == part.InsideOf);
            Assert.NotNull(part.Within);
            Assert.Equal(part.Within, sibling.Within);
            Assert.True(parts.IndexOf(part) < parts.IndexOf(sibling), $"{part.Name} — раньше соседки в каталоге");

            var zones = Names(root, sibling.List);

            foreach (var name in Names(root, part.List))
                Assert.Contains(zones, z => name == z || name.EndsWith("." + z, StringComparison.Ordinal));
        }

        static List<string> Names(string root, string list) =>
            File.ReadAllLines(Path.Combine(root, list))
                .Select(l => l.Trim().TrimStart('.'))
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetZapret.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("корень репозитория не найден");
    }

    [Fact]
    public void Every_within_names_a_list_of_the_catalog()
    {
        var lists = ServiceCatalog.All.SelectMany(s => s.Parts).Select(p => p.List).ToHashSet();

        foreach (var part in ServiceCatalog.All.SelectMany(s => s.Parts).Where(p => p.Within is not null))
            Assert.Contains(part.Within!, lists);
    }

    /// <summary>
    /// Голос ARC Raiders (07.10) — выключен до выбора, а выбранный встаёт раньше Epic.
    /// </summary>
    /// <remarks>
    /// Его зона rtcp.on.epicgames.com лежит внутри epicgames.com, а Epic у всех
    /// идёт «напрямую»: записанное после правило голоса не сработало бы никогда.
    /// </remarks>
    [Fact]
    public void Arc_raiders_voice_is_off_and_goes_before_epic()
    {
        var part = Assert.Single(ServiceCatalog.All.SelectMany(s => s.Parts),
            p => p.List == "config/lists/arc-raiders-voice.txt");

        Assert.True(part.DefaultOff);
        Assert.Equal("config/lists/epicgames-fortnite.txt", part.Within);
        Assert.EndsWith(".rtcp.on.epicgames.com", part.Probe);

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-rules-{Guid.NewGuid():N}.yaml");

        try
        {
            var file = UserRulesFile.Load(path);
            file.Set(MatchKind.HostList, "config/lists/epicgames-fortnite.txt", RoutingMode.Direct);
            file.Set(MatchKind.HostList, part.List, RoutingMode.Proxy, before: part.Within);

            Assert.Equal(
                ["config/lists/arc-raiders-voice.txt", "config/lists/epicgames-fortnite.txt"],
                file.Entries.Select(e => e.Value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Капча, выбранная отдельно, встаёт раньше списка, где её имя лежало прежде.
    /// </summary>
    [Fact]
    public void A_chosen_captcha_is_written_before_its_old_list()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-rules-{Guid.NewGuid():N}.yaml");

        try
        {
            var file = UserRulesFile.Load(path);
            file.Set(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Proxy);

            // Капчу выбрали отдельно — она встаёт раньше Discord и решает сама.
            file.Set(MatchKind.HostList, "config/lists/captcha-hcaptcha.txt", RoutingMode.Direct,
                before: "config/lists/discord.txt");

            Assert.Equal(
                ["config/lists/captcha-hcaptcha.txt", "config/lists/discord.txt"],
                file.Entries.Select(e => e.Value));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
