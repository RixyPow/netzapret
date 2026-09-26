using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Свои домены файлами списков.
/// </summary>
/// <remarks>
/// Просьба владельца 26.09: у своего домена — свой файл, как у части
/// сервиса, и удаляется он вместе с доменом. Здесь проверено и то, ради
/// чего это нельзя было сделать просто сменой типа правила: свой список
/// обязан побеждать список сервиса, как прежде побеждало своё имя.
/// </remarks>
public sealed class OwnDomainListsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}");

    public OwnDomainListsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Full(string value) => Path.Combine(_root, value);

    [Fact]
    public void A_domain_gets_its_own_file_with_its_name()
    {
        var value = OwnLists.Create("Example.com", _root);

        Assert.Equal("config/lists/own/example.com.txt", value);
        Assert.True(OwnLists.IsOwn(value));
        Assert.Equal("example.com", OwnLists.DomainOf(value));
        Assert.Contains("example.com", File.ReadAllLines(Full(value)));
    }

    /// <summary>Существующий файл не переписывается: его могли дополнить руками.</summary>
    [Fact]
    public void An_existing_file_is_kept()
    {
        var value = OwnLists.Create("example.com", _root);
        File.AppendAllText(Full(value), "cdn-example.net\r\n");

        OwnLists.Create("example.com", _root);

        Assert.Contains("cdn-example.net", File.ReadAllLines(Full(value)));
    }

    [Fact]
    public void Removing_deletes_the_file()
    {
        var value = OwnLists.Create("example.com", _root);

        OwnLists.Delete(value, _root);

        Assert.False(File.Exists(Full(value)));
    }

    /// <summary>Чужой список удалением своего домена не трогается никогда.</summary>
    [Fact]
    public void A_service_list_is_never_deleted()
    {
        var service = Full("config/lists/notion.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(service)!);
        File.WriteAllText(service, "notion.com\r\n");

        OwnLists.Delete("config/lists/notion.txt", _root);

        Assert.True(File.Exists(service));
    }

    /// <summary>
    /// Прежние свои домены переводятся в списки на своём месте, с режимом и рецептом.
    /// </summary>
    [Fact]
    public void Old_domains_move_to_lists_in_place()
    {
        var path = Full("rules.user.yaml");
        var file = UserRulesFile.Load(path);
        file.Set(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Desync);
        file.Set(MatchKind.Domain, "*.example.com", RoutingMode.Desync, recipe: "fake");
        file.Set(MatchKind.Domain, "*.other.org", RoutingMode.Proxy);

        Assert.Equal(2, OwnLists.Migrate(file, _root));
        file.Save();

        var entries = UserRulesFile.Load(path).Entries;

        Assert.Equal("config/lists/discord.txt", entries[0].Value);
        Assert.Equal(MatchKind.HostList, entries[1].Match);
        Assert.Equal("config/lists/own/example.com.txt", entries[1].Value);
        Assert.Equal("fake", entries[1].Recipe);
        Assert.Equal(RoutingMode.Proxy, entries[2].Mode);
        Assert.True(File.Exists(Full(entries[2].Value)));
        Assert.Equal(0, OwnLists.Migrate(UserRulesFile.Load(path), _root));
    }

    /// <summary>
    /// Свой список проверяется раньше списка сервиса — как прежде своё имя.
    /// </summary>
    /// <remarks>
    /// Решение владельца 23.09: имя, названное человеком поимённо, — его выбор.
    /// Без оговорки в Tier свой список встал бы в одну группу со списками
    /// сервисов, и победу решал бы порядок в файле.
    /// </remarks>
    [Fact]
    public void An_own_list_beats_a_service_list()
    {
        Assert.Equal(
            RuleEngine.Tier(MatchKind.Domain, RuleSource.User),
            RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/own/example.com.txt"));

        Assert.True(
            RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/own/example.com.txt")
            < RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/discord.txt"));

        var engine = RuleEngine.Build(
            [
                new RoutingRule { Match = MatchKind.HostList, Value = "config/lists/service.txt", Mode = RoutingMode.Direct, Source = RuleSource.User },
                new RoutingRule { Match = MatchKind.HostList, Value = "config/lists/own/example.com.txt", Mode = RoutingMode.Proxy, Source = RuleSource.User },
            ],
            RoutingMode.Desync);

        Assert.Equal("config/lists/own/example.com.txt", engine.RuleSet.Rules[0].Value);
    }
}
