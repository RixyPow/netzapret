using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Уведомление со звездой и каналом — после каждого обновления, тем, кто скрыл карточку (владелец, 03.10).
/// </summary>
public sealed class UpdateGreetingTests
{
    private static AppSettings Done(string? last) =>
        new AppSettings { OnboardingDone = true, SupportCardHidden = true, LastRunVersion = last };

    [Fact]
    public void A_new_version_is_greeted()
    {
        Assert.True(UpdateGreeting.Due(Done("0.11.2"), "0.11.3"));
    }

    [Fact]
    public void The_same_version_is_not_greeted_twice()
    {
        Assert.False(UpdateGreeting.Due(Done("0.11.3"), "0.11.3"));
    }

    /// <summary>Обновился с версии, которая ещё не запоминала себя, — тоже обновление.</summary>
    [Fact]
    public void Coming_from_a_version_that_did_not_remember_itself_is_greeted()
    {
        Assert.True(UpdateGreeting.Due(Done(null), "0.11.3"));
    }

    /// <summary>Только что поставил — не обновление: до звёзд человеку ещё не до того.</summary>
    [Fact]
    public void A_fresh_install_is_not_greeted()
    {
        Assert.False(UpdateGreeting.Due(new AppSettings { OnboardingDone = false }, "0.11.3"));
    }

    /// <summary>Карточка на «Главной» ещё стоит — та же просьба уже перед глазами (владелец, 03.10).</summary>
    [Fact]
    public void Not_greeted_while_the_home_card_is_shown()
    {
        Assert.False(UpdateGreeting.Due(Done("0.11.2") with { SupportCardHidden = false, GreetingForAll = true }, "0.11.3"));
    }

    /// <summary>
    /// Разово — всем, и тем, у кого карточка стоит (08.11, к 1.5.0); после этого — как прежде.
    /// </summary>
    [Fact]
    public void Once_greeted_even_with_the_home_card_then_back_to_the_rule()
    {
        var withCard = Done("1.4.1") with { SupportCardHidden = false };

        Assert.True(UpdateGreeting.Due(withCard, "1.5.0"));

        var after = UpdateGreeting.Seen(withCard, "1.5.0");

        Assert.Equal(true, after.GreetingForAll);
        Assert.False(UpdateGreeting.Due(after, "1.5.1"));
    }

    /// <summary>Первый запуск после установки тоже исчерпывает разовый показ: свежему он не нужен.</summary>
    [Fact]
    public void A_fresh_install_spends_the_one_time_greeting()
    {
        var fresh = UpdateGreeting.Seen(new AppSettings { OnboardingDone = false }, "1.5.0") with { OnboardingDone = true };

        Assert.False(UpdateGreeting.Due(fresh, "1.5.1"));
    }

    [Fact]
    public void Seen_remembers_the_version_and_keeps_the_rest()
    {
        var before = Done("0.11.2") with { PresetName = "Universal V10" };
        var after = UpdateGreeting.Seen(before, "0.11.3");

        Assert.Equal("0.11.3", after.LastRunVersion);
        Assert.Equal("Universal V10", after.PresetName);
        Assert.False(UpdateGreeting.Due(after, "0.11.3"));
    }
}
