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

    [Fact]
    public void Every_within_names_a_list_of_the_same_service()
    {
        foreach (var service in ServiceCatalog.All)
        {
            foreach (var part in service.Parts.Where(p => p.Within is not null))
                Assert.Contains(service.Parts, p => p.List == part.Within);
        }
    }
}
