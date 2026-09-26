using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Свои списки: файл своему домену — по просьбе, с названием на выбор.
/// </summary>
/// <remarks>
/// Владелец 26.09: файл не заводится сам — одно имя в двух файлах даёт два
/// спорящих правила. Здесь же проверено то, ради чего это нельзя было сделать
/// просто сменой типа правила: свой список обязан побеждать список сервиса,
/// как прежде побеждало своё имя.
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
    public void A_list_is_created_under_the_chosen_name_with_the_domain_inside()
    {
        var value = OwnLists.Create("мой-сайт", ["*.example.com"], _root);

        Assert.Equal("config/lists/own/мой-сайт.txt", value);
        Assert.True(OwnLists.IsOwn(value));
        Assert.Equal("мой-сайт", OwnLists.NameOf(value));
        Assert.Contains("example.com", File.ReadAllLines(Full(value)));
    }

    /// <summary>Список с тем же названием не переписывается: в нём могут быть свои имена.</summary>
    [Fact]
    public void An_existing_list_is_not_overwritten()
    {
        var value = OwnLists.Create("site", ["example.com"], _root);
        File.AppendAllText(Full(value), "cdn-example.net\r\n");

        Assert.True(OwnLists.Exists("site", _root));
        Assert.Throws<IOException>(() => OwnLists.Create("site", ["other.org"], _root));
        Assert.Contains("cdn-example.net", File.ReadAllLines(Full(value)));
    }

    [Theory]
    [InlineData("Мои сайты", "мои-сайты")]
    [InlineData("example.com", "example.com")]
    [InlineData("list.txt", "list")]
    public void A_name_is_brought_to_a_file_name(string typed, string expected)
    {
        Assert.Equal(expected, OwnLists.Normalize(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("con:")]
    public void A_bad_name_is_refused(string typed)
    {
        Assert.Null(OwnLists.Normalize(typed));
    }

    [Fact]
    public void Removing_deletes_the_file()
    {
        var value = OwnLists.Create("site", ["example.com"], _root);

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
    /// Всё своё идёт в проверку блокировок: домен одним именем, список — первыми.
    /// </summary>
    /// <remarks>
    /// Владелец 26.09: «всё, что мы добавляем, должно быть и в блокчеке».
    /// Свой список «harambaby» в отчёт не попал — проверка знала только каталог.
    /// </remarks>
    [Fact]
    public void Own_domains_and_lists_go_to_the_check()
    {
        var list = OwnLists.Create("harambaby", ["*.one.example", "two.example", "three.example"], _root);

        var file = UserRulesFile.Load(Full("rules.user.yaml"));
        file.Set(MatchKind.Domain, "*.own.example", RoutingMode.Proxy);
        file.Set(MatchKind.HostList, list, RoutingMode.Proxy);
        file.Set(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Desync);
        file.Set(MatchKind.Domain, "*.off.example", RoutingMode.Proxy);
        file.Toggle(3);

        var targets = OwnLists.CheckTargets(file, perList: 2, _root);

        Assert.Equal(
            [
                ("own.example", "свой домен"),
                ("one.example", "свой · harambaby"),
                ("two.example", "свой · harambaby"),
            ],
            targets);
    }

    /// <summary>Комментарии и пустые строки своего списка — не имена.</summary>
    [Fact]
    public void Comments_are_not_names()
    {
        var list = OwnLists.Create("site", ["example.com"], _root);
        File.AppendAllText(Full(list), "\r\n# заметка\r\ncdn.example.net # хвост\r\n");

        Assert.Equal(["example.com", "cdn.example.net"], OwnLists.Names(list, _root));
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
            RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/own/site.txt"));

        Assert.True(
            RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/own/site.txt")
            < RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/discord.txt"));

        var engine = RuleEngine.Build(
            [
                new RoutingRule { Match = MatchKind.HostList, Value = "config/lists/service.txt", Mode = RoutingMode.Direct, Source = RuleSource.User },
                new RoutingRule { Match = MatchKind.HostList, Value = "config/lists/own/site.txt", Mode = RoutingMode.Proxy, Source = RuleSource.User },
            ],
            RoutingMode.Desync);

        Assert.Equal("config/lists/own/site.txt", engine.RuleSet.Rules[0].Value);
    }
}
