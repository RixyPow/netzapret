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
    [Fact]
    public void Every_within_names_a_list_of_the_catalog()
    {
        var lists = ServiceCatalog.All.SelectMany(s => s.Parts).Select(p => p.List).ToHashSet();

        foreach (var part in ServiceCatalog.All.SelectMany(s => s.Parts).Where(p => p.Within is not null))
            Assert.Contains(part.Within!, lists);
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
