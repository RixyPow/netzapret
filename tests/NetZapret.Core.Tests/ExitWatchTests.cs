using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сторож выхода: замена молчащему серверу и возврат выбранного руками (30.09).
/// </summary>
public sealed class ExitWatchTests
{
    private const string Pinned = "🇪🇪 Эстония";
    private const string Other = "🇩🇪 Германия";

    [Fact]
    public void AutoPick_replaces_after_two_misses_in_a_row()
    {
        var watch = new ExitWatch(null, replacePinned: false);

        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));
        Assert.True(watch.Rechecking);
        Assert.Equal(ExitAction.Replace, watch.OnCurrent(answers: false));
        Assert.False(watch.Rechecking);
    }

    [Fact]
    public void One_miss_is_forgiven()
    {
        var watch = new ExitWatch(null, replacePinned: false);

        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));
        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: true));
        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));
    }

    /// <summary>Настройка выключена — выбранный руками сервер не меняется, как до 30.09.</summary>
    [Fact]
    public void A_pinned_server_is_left_alone_without_the_setting()
    {
        var watch = new ExitWatch(Pinned, replacePinned: false);

        for (int i = 0; i < 10; i++)
            Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));

        Assert.False(watch.Rechecking);
        Assert.False(watch.Replaced(Other));
    }

    [Fact]
    public void A_pinned_server_is_replaced_after_two_misses_with_the_setting()
    {
        var watch = new ExitWatch(Pinned, replacePinned: true);

        Assert.False(watch.Replaced(Pinned));
        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));
        Assert.Equal(ExitAction.Replace, watch.OnCurrent(answers: false));
        Assert.True(watch.Replaced(Other));
    }

    /// <summary>Один ответ — не возврат: так отвечает и «мигающий» сервер.</summary>
    [Fact]
    public void The_pinned_server_returns_after_two_answers_in_a_row()
    {
        var watch = new ExitWatch(Pinned, replacePinned: true);

        Assert.Equal(ExitAction.Keep, watch.OnPinned(answers: true));
        Assert.Equal(ExitAction.Keep, watch.OnPinned(answers: false));
        Assert.Equal(ExitAction.Keep, watch.OnPinned(answers: true));
        Assert.Equal(ExitAction.Restore, watch.OnPinned(answers: true));
    }

    /// <summary>Замена тоже может умереть — тогда ищем следующую, как при автоподборе.</summary>
    [Fact]
    public void A_dead_replacement_is_replaced_too()
    {
        var watch = new ExitWatch(Pinned, replacePinned: true);

        Assert.True(watch.Replaced(Other));
        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));
        Assert.Equal(ExitAction.Replace, watch.OnCurrent(answers: false));
    }

    /// <summary>Счёт ответов выбранного не переживает новую замену: возврат — только по двум свежим.</summary>
    [Fact]
    public void A_new_replacement_restarts_the_count_of_answers()
    {
        var watch = new ExitWatch(Pinned, replacePinned: true);

        Assert.Equal(ExitAction.Keep, watch.OnPinned(answers: true));

        watch.OnCurrent(answers: false);
        Assert.Equal(ExitAction.Replace, watch.OnCurrent(answers: false));

        Assert.Equal(ExitAction.Keep, watch.OnPinned(answers: true));
        Assert.Equal(ExitAction.Restore, watch.OnPinned(answers: true));
    }

    [Fact]
    public void No_replacement_is_seen_while_the_engine_is_silent_or_on_auto()
    {
        Assert.False(new ExitWatch(Pinned, replacePinned: true).Replaced(null));
        Assert.False(new ExitWatch(null, replacePinned: true).Replaced(Other));
        Assert.False(new ExitWatch("  ", replacePinned: true).Replaced(Other));
    }

    [Fact]
    public void A_restart_forgets_the_misses()
    {
        var watch = new ExitWatch(null, replacePinned: false);

        watch.OnCurrent(answers: false);
        watch.Forget();

        Assert.False(watch.Rechecking);
        Assert.Equal(ExitAction.Keep, watch.OnCurrent(answers: false));
    }
}
