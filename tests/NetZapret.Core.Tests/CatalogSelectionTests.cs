using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверяет, что бесплодный выбор в каталоге не остаётся безмолвным.
/// </summary>
/// <remarks>
/// Живой случай. В настройках стояло семь выбранных сервисов, набор адресов
/// выбран не был, и подстановок выходило ноль: сервисы вида <c>dns</c> берут
/// адрес из набора, и без него запрос к базе не выполняется. Человек видел
/// семь галочек и был уверен, что каталог работает; ChatGPT у него починился
/// совсем другим путём, а этот молчал.
/// </remarks>
public class CatalogSelectionTests
{
    [Fact]
    public void Services_without_a_set_are_explained()
    {
        var why = CatalogSelection.Explain(services: 7, profile: null, answers: 0);

        Assert.NotNull(why);
        Assert.Contains("набор", why);
        Assert.Contains("7", why);
    }

    /// <remarks>Пустая строка в настройках означает то же, что и отсутствие.</remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_set_counts_as_missing(string profile)
    {
        var why = CatalogSelection.Explain(services: 3, profile: profile, answers: 0);

        Assert.NotNull(why);
        Assert.Contains("не выбран", why);
    }

    /// <summary>Набор выбран, а адресов нет — причина другая, и совет другой.</summary>
    [Fact]
    public void A_set_that_covers_nothing_says_so()
    {
        var why = CatalogSelection.Explain(services: 3, profile: "xbox_dns", answers: 0);

        Assert.NotNull(why);
        Assert.Contains("xbox_dns", why);
        Assert.Contains("другой набор", why);
    }

    /// <summary>Когда всё сработало, говорить нечего.</summary>
    [Fact]
    public void A_working_selection_is_silent()
    {
        Assert.Null(CatalogSelection.Explain(services: 7, profile: "xbox_dns", answers: 41));
    }

    /// <summary>Ничего не выбрано — это не беда, а выбор.</summary>
    [Fact]
    public void Choosing_nothing_is_not_a_problem()
    {
        Assert.Null(CatalogSelection.Explain(services: 0, profile: null, answers: 0));
    }
}
