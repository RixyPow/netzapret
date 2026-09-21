using NetZapret.Core;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Выключатели движков доходят до запуска.
/// </summary>
/// <remarks>
/// <para>
/// Самая дорогая проверка из всей седьмой цели, и заведена она по найденной
/// дыре. Выключатели появились 21.09, а запуск по-прежнему спрашивал режим:
/// <c>NeedsDesync</c> проверял «режим не выключен и пресет задан», и
/// выключенный десинк поднимался бы как ни в чём не бывало.
/// </para>
/// <para>
/// Узнать об этом можно было бы только по живому процессу winws2 при
/// погашенном выключателе — то есть не узнать вовсе, пока не полезешь
/// в диспетчер задач.
/// </para>
/// <para>
/// Глубже причина в том, что сочетаний четыре, а режимов пять, и одно
/// из сочетаний режимом не выражается вовсе: «туннель по маршрутам без
/// десинка». Спрашивать режим о том, чего он не знает, значит получать
/// неверный ответ молча.
/// </para>
/// </remarks>
public sealed class EnginesReachLaunchTests
{
    private static AppSettings Both() => new()
    {
        SubscriptionUrl = "https://example.invalid/sub",
        PresetName = "Universal V9",
    };

    [Fact]
    public void Both_switches_on_raise_both()
    {
        var settings = Both().With(new EngineChoice { Desync = true, Tunnel = true });

        Assert.True(settings.NeedsDesync);
        Assert.True(settings.NeedsProxy);
    }

    [Fact]
    public void Desync_off_stops_winws()
    {
        // Ровно та дыра: прежде здесь было true, потому что режим
        // при выключенном десинке остаётся ProxyAll, а условие пропускало
        // его насквозь.
        var settings = Both().With(new EngineChoice { Desync = false, Tunnel = true });

        Assert.False(settings.NeedsDesync);
        Assert.True(settings.NeedsProxy);
    }

    [Fact]
    public void Tunnel_off_stops_sing_box()
    {
        var settings = Both().With(new EngineChoice { Desync = true, Tunnel = false });

        Assert.True(settings.NeedsDesync);
        Assert.False(settings.NeedsProxy);
    }

    [Fact]
    public void Both_off_raise_nothing()
    {
        var settings = Both().With(new EngineChoice { Desync = false, Tunnel = false });

        Assert.False(settings.NeedsDesync);
        Assert.False(settings.NeedsProxy);
    }

    [Fact]
    public void The_combination_that_no_mode_can_express_works_too()
    {
        // «Туннель по маршрутам без десинка»: режимов пять, сочетаний
        // четыре, и вот это среди режимов отсутствует. Его выведенный
        // режим — ProxyAll, то есть «туннель забирает всё», и по режиму
        // ответ был бы про другое состояние.
        var settings = Both().With(new EngineChoice { Desync = false, Tunnel = true });

        Assert.True(settings.Engines.TunnelTakesAll);
        Assert.False(settings.NeedsDesync);
    }

    [Fact]
    public void Without_a_preset_desync_is_not_raised_even_switched_on()
    {
        // Поднимать winws2 без пресета нечем: ему нечего применять.
        var settings = new AppSettings { PresetName = null }
            .With(new EngineChoice { Desync = true, Tunnel = false });

        Assert.False(settings.NeedsDesync);
    }

    [Fact]
    public void Without_a_subscription_the_tunnel_is_not_raised_in_the_selective_case()
    {
        // Выборочный без выхода — это просто десинк, и поднимать ради него
        // TUN незачем: адаптер есть, маршруты стоят, трафик через них
        // не идёт.
        var settings = new AppSettings { SubscriptionUrl = null, PresetName = "Universal V9" }
            .With(new EngineChoice { Desync = true, Tunnel = true });

        Assert.False(settings.NeedsProxy);
        Assert.True(settings.NeedsDesync);
    }

    [Fact]
    public void Settings_from_before_the_switches_still_raise_the_right_things()
    {
        // Переход: у людей записан режим и больше ничего.
        var old = new AppSettings
        {
            Mode = OperatingMode.Selective,
            SubscriptionUrl = "https://example.invalid/sub",
            PresetName = "Universal V9",
        };

        Assert.True(old.NeedsDesync);
        Assert.True(old.NeedsProxy);
    }
}
