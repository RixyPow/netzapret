using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Имена, названные дважды и по-разному.
/// </summary>
/// <remarks>
/// <para>
/// Ради этого книга маршрутов и заводилась — не ради удобства, а ради
/// того, чтобы молчаливая поломка стала громкой.
/// </para>
/// <para>
/// 21.09 у владельца Instagram был прибит в hosts и потому выведен
/// из-под десинка целиком: multidisorder к нему не применялся ни разу.
/// Проверка рецептов при этом показывала, что рецепт работает, — она
/// поднимает свой winws2 без списка исключений. Разбор занял час;
/// снятие пина починило и Instagram, и Facebook.
/// </para>
/// <para>
/// Ни один из пяти источников правил не мог этого сказать: они не сведены,
/// и порядок между ними лежит в коде.
/// </para>
/// </remarks>
public sealed class RouteClashTests
{
    private static RouteEntry Row(string name, RouteChoice choice) =>
        new() { Name = name, Choice = choice };

    [Fact]
    public void A_pin_and_a_tunnel_on_one_name_are_named_a_clash()
    {
        // Тот самый случай. Прибитое имя не получает fakeip, а без fakeip
        // движок не заводит его в туннель — правило не делает ничего.
        var found = RouteClashes.Find([
            Row("instagram", RouteChoice.Pin),
            Row("instagram", RouteChoice.Vpn),
        ]);

        var clash = Assert.Single(found);

        Assert.Equal("instagram", clash.Name);
        Assert.Contains("fakeip", clash.Outcome);
    }

    [Fact]
    public void A_pin_and_a_desync_are_named_too()
    {
        // Вторая половина того же случая, и именно она стоила часа:
        // рецепт был выбран, показан в окне и не применялся ни разу.
        var found = RouteClashes.Find([
            Row("instagram", RouteChoice.Pin),
            Row("instagram", RouteChoice.Desync),
        ]);

        Assert.Contains("из-под десинка", Assert.Single(found).Outcome);
    }

    [Fact]
    public void A_repeat_of_the_same_route_is_not_a_clash()
    {
        // Лишняя строка ничего не меняет и ничего не ломает. Жаловаться
        // на неё — размывать список настоящих бед.
        Assert.Empty(RouteClashes.Find([
            Row("discord", RouteChoice.Desync),
            Row("discord", RouteChoice.Desync),
        ]));
    }

    [Fact]
    public void A_clean_book_has_nothing_to_say()
    {
        Assert.Empty(RouteClashes.Find([
            Row("discord", RouteChoice.Desync),
            Row("github", RouteChoice.Direct),
            Row("linkedin", RouteChoice.Vpn),
            Row("tmdb", RouteChoice.Pin),
        ]));
    }

    [Fact]
    public void Every_outcome_says_what_will_happen()
    {
        // Не «конфликт»: человеку нужно знать не то, что мы растерялись,
        // а что произойдёт с его именем.
        foreach (RouteChoice one in Enum.GetValues<RouteChoice>())
        {
            foreach (RouteChoice other in Enum.GetValues<RouteChoice>())
            {
                if (one == other)
                    continue;

                var said = RouteClashes.Outcome(one, other);

                Assert.False(string.IsNullOrWhiteSpace(said));
                Assert.DoesNotContain("конфликт", said, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_outcome_does_not_depend_on_the_order()
    {
        // Пин с туннелем и туннель с пином — одна и та же беда.
        Assert.Equal(
            RouteClashes.Outcome(RouteChoice.Pin, RouteChoice.Vpn),
            RouteClashes.Outcome(RouteChoice.Vpn, RouteChoice.Pin));
    }

    [Fact]
    public void A_domain_pinned_against_its_own_group_is_found()
    {
        // Случай на стыке и самый частый из настоящих: точного повтора
        // нет — имена разные, — а спорят они об одном соединении.
        var found = RouteClashes.Find(
            [Row("instagram", RouteChoice.Desync), Row("instagram.com", RouteChoice.Pin)],
            group => group == "instagram" ? ["instagram.com", "www.instagram.com"] : []);

        var clash = Assert.Single(found);

        Assert.Equal("instagram.com", clash.Name);
        Assert.Contains("instagram", clash.Outcome);
    }

    [Fact]
    public void A_domain_merely_refined_against_its_group_is_left_alone()
    {
        // Отдельная строка на домен затем и нужна, чтобы вывести его
        // из общего правила группы, и побеждает она. Это уточнение,
        // а не спор.
        var found = RouteClashes.Find(
            [Row("discord", RouteChoice.Desync), Row("discord.com", RouteChoice.Vpn)],
            group => group == "discord" ? ["discord.com", "discordapp.com"] : []);

        Assert.Empty(found);
    }

    [Fact]
    public void Without_group_contents_only_exact_repeats_are_found()
    {
        // Каталога может не быть под рукой — это не повод молчать
        // о том, что видно и без него.
        var found = RouteClashes.Find([
            Row("instagram", RouteChoice.Desync),
            Row("instagram.com", RouteChoice.Pin),
        ]);

        Assert.Empty(found);
    }

    [Theory]
    [InlineData(RouteChoice.Pin, RouteChoice.Vpn, true)]
    [InlineData(RouteChoice.Pin, RouteChoice.Desync, true)]
    [InlineData(RouteChoice.Vpn, RouteChoice.Desync, false)]
    [InlineData(RouteChoice.Direct, RouteChoice.Vpn, false)]
    [InlineData(RouteChoice.Direct, RouteChoice.Desync, false)]
    [InlineData(RouteChoice.Pin, RouteChoice.Direct, false)]
    public void Incompatibility_is_about_the_pin(RouteChoice one, RouteChoice other, bool clash)
    {
        // Пин несовместим со всем, что требует видеть имя: он задаёт адрес,
        // и после него ни туннель, ни десинк до имени не доберутся.
        // С «напрямую» он уживается — оба означают «мимо всего».
        Assert.Equal(clash, RouteClashes.Incompatible(one, other));
    }
}
