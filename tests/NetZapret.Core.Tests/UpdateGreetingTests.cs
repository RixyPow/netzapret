using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Уведомление со звездой и каналом — после каждого обновления (владелец, 03.10).
/// </summary>
public sealed class UpdateGreetingTests
{
    private static AppSettings Done(string? last) =>
        new AppSettings { OnboardingDone = true, LastRunVersion = last };

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
