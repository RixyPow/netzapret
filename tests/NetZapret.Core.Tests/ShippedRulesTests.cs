using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Поставляемые файлы правил разбираются.
/// </summary>
/// <remarks>
/// <para>
/// Опечатка в YAML не ломает компиляцию, но делает программу неработоспособной
/// у всех, кто её скачал. Прежде это проверял шаг CI, гонявший консольную
/// команду rules по каждому config/*.yaml. Консоль уходит, и проверка
/// переехала сюда — тем же вызовом, которым rules и читала файл.
/// </para>
/// <para>
/// Здесь она вдобавок гоняется локально, до сборки, а не только в CI.
/// </para>
/// </remarks>
public sealed class ShippedRulesTests
{
    private static string? Config()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return Path.Combine(directory.FullName, "config");
        }

        return null;
    }

    [Fact]
    public void Every_shipped_yaml_parses()
    {
        var config = Config();
        if (config is null)
            return;

        // rules.user.yaml — личный файл владельца, в репозиторий не входит
        // и проверке не подлежит: у каждого он свой.
        var files = Directory.GetFiles(config, "*.yaml")
            .Where(f => !Path.GetFileName(f).Equals("rules.user.yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(files);

        var broken = new List<string>();

        foreach (var file in files)
        {
            try
            {
                RuleSetLoader.LoadFromFile(file);
            }
            catch (Exception ex)
            {
                broken.Add($"{Path.GetFileName(file)}: {ex.GetBaseException().Message}");
            }
        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    [Fact]
    public void The_base_rules_are_not_empty()
    {
        // Разобраться в ноль правил — тоже поломка, и молчаливая:
        // программа работает, только ничего никуда не ведёт.
        var config = Config();
        if (config is null)
            return;

        Assert.NotEmpty(RuleSetLoader.LoadFromFile(Path.Combine(config, "rules.yaml")).RuleSet.Rules);
    }

    /// <summary>
    /// Twitch — напрямую по умолчанию: под секцией «Twitch» пресета эфиры не играют
    /// (ошибка #2000, жалоба и замер 03.10).
    /// </summary>
    [Fact]
    public void Twitch_goes_around_the_desync_by_default()
    {
        var config = Config();
        if (config is null)
            return;

        var engine = RuleSetLoader.LoadFromFile(Path.Combine(config, "rules.yaml"));
        NetZapret.Zapret.RuleSetExpander.Expand(engine.RuleSet, Path.GetDirectoryName(config));

        var shield = NetZapret.Proxy.HostsFile.DescribeDesyncExclusions(
            engine.RuleSet, hostsPath: Path.Combine(Path.GetTempPath(), $"no-hosts-{Guid.NewGuid():N}"));

        foreach (var host in new[] { "www.twitch.tv", "usher.ttvnw.net", "eun12.playlist.ttvnw.net", "static-cdn.jtvnw.net" })
            Assert.Equal(NetZapret.Proxy.DesyncBypass.Direct, NetZapret.Proxy.HostsFile.BypassFor(shield, host));
    }

    /// <summary>
    /// «Эфиры» на «десинке», сайт — как был: видео и hermes под десинк, сайт в щите
    /// (03.10: у пользователя с #2000 Amazon напрямую не доходит, сайт на Fastly — да).
    /// </summary>
    [Fact]
    public void Twitch_streams_can_take_their_own_path()
    {
        var config = Config();
        if (config is null)
            return;

        var user = Path.Combine(Path.GetTempPath(), $"rules-user-{Guid.NewGuid():N}.yaml");

        try
        {
            File.WriteAllText(user, """
                rules:
                  - match: hostlist
                    value: "config/lists/twitch-video.txt"
                    mode: desync
                """);

            var engine = RuleSetLoader.LoadLayered(Path.Combine(config, "rules.yaml"), user);
            NetZapret.Zapret.RuleSetExpander.Expand(engine.RuleSet, Path.GetDirectoryName(config));

            static Connections.ConnectionEvent To(string host) => new()
            {
                Timestamp = DateTimeOffset.UnixEpoch,
                Protocol = Connections.ProtocolKind.Tcp,
                RemoteAddress = System.Net.IPAddress.Parse("203.0.113.7"),
                RemotePort = 443,
                Hostname = host,
            };

            Assert.Equal(RoutingMode.Desync, engine.Evaluate(To("usher.ttvnw.net")).Mode);
            Assert.Equal(RoutingMode.Desync, engine.Evaluate(To("hermes.twitch.tv")).Mode);
            Assert.Equal(RoutingMode.Direct, engine.Evaluate(To("www.twitch.tv")).Mode);

            var shield = NetZapret.Proxy.HostsFile.DescribeDesyncExclusions(
                engine.RuleSet, hostsPath: Path.Combine(Path.GetTempPath(), $"no-hosts-{Guid.NewGuid():N}"));
            var holes = NetZapret.Proxy.HostsFile.CollectShieldHoles(engine.RuleSet, shield);

            Assert.Equal(NetZapret.Proxy.DesyncBypass.None, NetZapret.Proxy.HostsFile.BypassFor(shield, "usher.ttvnw.net", holes));
            Assert.Equal(NetZapret.Proxy.DesyncBypass.None, NetZapret.Proxy.HostsFile.BypassFor(shield, "hermes.twitch.tv", holes));
            Assert.Equal(NetZapret.Proxy.DesyncBypass.Direct, NetZapret.Proxy.HostsFile.BypassFor(shield, "www.twitch.tv", holes));
        }
        finally
        {
            File.Delete(user);
        }
    }

    /// <summary>
    /// Зоны Akamai — в щите «напрямую», а имена Spotify и TikTok на них —
    /// за своими частями (03.10, картинки Battle.net под приёмом секции Fortnite).
    /// </summary>
    [Fact]
    public void Akamai_zones_are_shielded_but_named_services_keep_their_parts()
    {
        var config = Config();
        if (config is null)
            return;

        var engine = RuleSetLoader.LoadFromFile(Path.Combine(config, "rules.yaml"));
        NetZapret.Zapret.RuleSetExpander.Expand(engine.RuleSet, Path.GetDirectoryName(config));

        var shield = NetZapret.Proxy.HostsFile.DescribeDesyncExclusions(
            engine.RuleSet, hostsPath: Path.Combine(Path.GetTempPath(), $"no-hosts-{Guid.NewGuid():N}"));

        Assert.Contains(shield, each => each.Name == "akamaized.net" && each.Why == NetZapret.Proxy.DesyncBypass.Direct);
        Assert.Contains(shield, each => each.Name == "akamaihd.net" && each.Why == NetZapret.Proxy.DesyncBypass.Direct);

        static Connections.ConnectionEvent To(string host) => new()
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Protocol = Connections.ProtocolKind.Tcp,
            RemoteAddress = System.Net.IPAddress.Parse("203.0.113.7"),
            RemotePort = 443,
            Hostname = host,
        };

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(To("blz-contentstack-images.akamaized.net")).Mode);
        Assert.Equal(RoutingMode.Direct, engine.Evaluate(To("bnetcmsus-a.akamaihd.net")).Mode);

        // Путь, выбранный человеком части со своими именами на Akamai, бьёт зону:
        // слой человека проверяется раньше базового.
        var user = Path.Combine(Path.GetTempPath(), $"rules-user-{Guid.NewGuid():N}.yaml");

        try
        {
            File.WriteAllText(user, """
                rules:
                  - match: hostlist
                    value: "config/lists/spotify-cdn.txt"
                    mode: proxy
                """);

            var layered = RuleSetLoader.LoadLayered(Path.Combine(config, "rules.yaml"), user);
            NetZapret.Zapret.RuleSetExpander.Expand(layered.RuleSet, Path.GetDirectoryName(config));

            Assert.Equal(RoutingMode.Proxy, layered.Evaluate(To("audio-ak-spotify-com.akamaized.net")).Mode);
            Assert.Equal(RoutingMode.Direct, layered.Evaluate(To("blz-contentstack-images.akamaized.net")).Mode);

            // «Десинк», выбранный человеком, — дыра в щите зоны: приём к нему вернётся.
            File.WriteAllText(user, """
                rules:
                  - match: hostlist
                    value: "config/lists/spotify-cdn.txt"
                    mode: desync
                """);

            var kept = RuleSetLoader.LoadLayered(Path.Combine(config, "rules.yaml"), user);
            NetZapret.Zapret.RuleSetExpander.Expand(kept.RuleSet, Path.GetDirectoryName(config));

            var exclusions = NetZapret.Proxy.HostsFile.DescribeDesyncExclusions(
                kept.RuleSet, hostsPath: Path.Combine(Path.GetTempPath(), $"no-hosts-{Guid.NewGuid():N}"));
            var holes = NetZapret.Proxy.HostsFile.CollectShieldHoles(kept.RuleSet, exclusions);

            Assert.Contains("audio-ak-spotify-com.akamaized.net", holes);
            Assert.Equal(NetZapret.Proxy.DesyncBypass.None,
                NetZapret.Proxy.HostsFile.BypassFor(exclusions, "audio-ak-spotify-com.akamaized.net", holes));
            Assert.Equal(NetZapret.Proxy.DesyncBypass.Direct,
                NetZapret.Proxy.HostsFile.BypassFor(exclusions, "blz-contentstack-images.akamaized.net", holes));
        }
        finally
        {
            File.Delete(user);
        }
    }
}
