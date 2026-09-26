using NetZapret.Core.Rules;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Свои домены стоят в общем списке наравне с каталожными.
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 20.09: «хочу, чтоб вместо „свой домен“ кастомные
/// домены добавлялись в список сервисов наряду с остальными, чтобы им
/// также можно было дать пин, видеть иконку и т. д.»
/// </para>
/// <para>
/// Проверяется ключ строки. Он один на все действия над ней — пин,
/// снятие пина, выбор маршрута, выбор рецепта, удаление, — и по нему
/// обработчик решает, какое правило писать и где искать зоны. Ошибка
/// здесь не роняет окно: правило просто ляжет не того рода, и «убрать»
/// потом не найдёт того, что записано.
/// </para>
/// </remarks>
public sealed class OwnDomainRowTests
{
    /// <summary>
    /// Как раздел записывает добавленное имя.
    /// </summary>
    /// <remarks>
    /// Со звёздочкой: имя означает зону, и <c>example.com</c> обязан
    /// покрывать <c>cdn.example.com</c>.
    /// </remarks>
    private const string Rule = "*.example.com";

    /// <summary>Свой домен прежнего вида — голое имя — остаётся domain-правилом.</summary>
    [Fact]
    public void An_old_custom_domain_writes_a_domain_rule()
    {
        // Не HostList. Файла у такого домена нет, и правило со ссылкой
        // на несуществующий файл не совпало бы ни с чем — молча.
        var key = RouteKeys.Make(RouteKeys.Own, Rule);
        var parsed = RouteKeys.Parse(key);

        Assert.NotNull(parsed);
        Assert.Equal(RouteKeys.Own, parsed.Value.Kind);
        Assert.Equal(Rule, parsed.Value.Value);
        Assert.Equal(MatchKind.Domain, RouteKeys.MatchOf(parsed.Value.Kind, parsed.Value.Value));
    }

    /// <summary>
    /// Свой домен с 26.09 — файл списка, и правило на него hostlist.
    /// </summary>
    [Fact]
    public void A_custom_domain_with_its_list_writes_a_hostlist_rule()
    {
        Assert.Equal(MatchKind.HostList,
            RouteKeys.MatchOf(RouteKeys.Own, "config/lists/own/example.com.txt"));
    }

    [Fact]
    public void Catalogue_parts_keep_their_own_kinds()
    {
        Assert.Equal(MatchKind.HostList, RouteKeys.MatchOf(RouteKeys.HostList, "config/lists/discord.txt"));
        Assert.Equal(MatchKind.IpSet, RouteKeys.MatchOf(RouteKeys.IpSet, "config/lists/ipset-discord.txt"));
    }

    [Fact]
    public void A_value_with_separators_survives_parsing()
    {
        // Путь к списку — config/lists/x.txt, и разделителей в нём нет,
        // но разбор всё равно идёт на две части, а не на все: ключ,
        // разобранный на три куска, потерял бы хвост значения.
        var key = RouteKeys.Make(RouteKeys.HostList, "config/lists/a|b.txt");

        Assert.Equal("config/lists/a|b.txt", RouteKeys.Parse(key)!.Value.Value);
    }

    [Fact]
    public void Nonsense_is_not_parsed()
    {
        Assert.Null(RouteKeys.Parse(null));
        Assert.Null(RouteKeys.Parse(string.Empty));
        Assert.Null(RouteKeys.Parse("без разделителя"));
        Assert.Null(RouteKeys.Parse("|значение"));
    }

    [Fact]
    public void A_custom_domain_is_its_own_zone()
    {
        // У каталожной части зоны читаются из файла списка, у своего
        // домена файла нет вовсе. Пойди этот путь по общей ветке —
        // читался бы несуществующий файл, зоны вышли бы пустыми,
        // и кнопка сказала бы «пина нет» над живым пином.
        var zones = RouteKeys.Zones(RouteKeys.Make(RouteKeys.Own, Rule), zapretRoot: null);

        Assert.Equal(["example.com"], zones);
    }

    [Fact]
    public void A_custom_domain_covers_its_subdomains()
    {
        var zones = RouteKeys.Zones(RouteKeys.Make(RouteKeys.Own, Rule), zapretRoot: null);

        Assert.True(RouteKeys.Covers(zones, "example.com"));
        Assert.True(RouteKeys.Covers(zones, "cdn.example.com"));
        Assert.False(RouteKeys.Covers(zones, "notexample.com"));
        Assert.False(RouteKeys.Covers(zones, "example.com.evil.ru"));
    }

    [Fact]
    public void An_unreadable_list_gives_empty_zones_instead_of_throwing()
    {
        // Раздел показывается и без установленного Zapret.
        var zones = RouteKeys.Zones(
            RouteKeys.Make(RouteKeys.HostList, "такого/файла/нет.txt"),
            zapretRoot: null);

        Assert.Empty(zones);
    }

    [Fact]
    public void A_recipe_is_asked_for_names_and_not_for_addresses()
    {
        // Рецепт применяется по имени в приветствии TLS. У правила
        // по адресу имени нет, и любой рецепт отвечал бы «не помогает».
        Assert.True(RouteKeys.TakesRecipe(RouteKeys.Own));
        Assert.True(RouteKeys.TakesRecipe(RouteKeys.HostList));
        Assert.False(RouteKeys.TakesRecipe(RouteKeys.IpSet));
    }
}
