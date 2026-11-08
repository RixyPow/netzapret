using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Карточка «Нравится NetZapret?» на «Главной» — только при включённом показе (владелец, 08.11).
/// </summary>
public sealed class SupportAskTests
{
    private static readonly AppSettings Done = new() { OnboardingDone = true };

    /// <summary>Пока показ не включён, карточки нет ни у кого — и у не скрывавших её.</summary>
    [Fact]
    public void No_campaign_no_card()
    {
        Assert.False(SupportAsk.Shown(Done, campaign: null));
    }

    /// <summary>Включённый показ видят все, и скрывшие прежнюю карточку (до 08.11 — навсегда).</summary>
    [Fact]
    public void A_campaign_shows_the_card_even_to_those_who_hid_the_old_one()
    {
        Assert.True(SupportAsk.Shown(Done, "1.6.0"));
        Assert.True(SupportAsk.Shown(Done with { SupportCardHidden = true }, "1.6.0"));
    }

    [Fact]
    public void Hiding_lasts_until_the_next_campaign()
    {
        var hidden = SupportAsk.Hide(Done, "1.6.0");

        Assert.False(SupportAsk.Shown(hidden, "1.6.0"));
        Assert.True(SupportAsk.Shown(hidden, "1.7.0"));
    }

    /// <summary>До мастера первого запуска человеку не до звёзд.</summary>
    [Fact]
    public void Not_before_the_first_run_wizard()
    {
        Assert.False(SupportAsk.Shown(new AppSettings { OnboardingDone = false }, "1.6.0"));
    }
}
