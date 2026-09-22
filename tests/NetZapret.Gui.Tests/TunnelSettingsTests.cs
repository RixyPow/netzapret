using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Окно настроек туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 21.09: вкладка «VPN» разрослась, и шесть карточек настроек
/// оттесняли за нижний край список подписок, ради которого на неё
/// и заходят.
/// </para>
/// <para>
/// Список режимов отсюда ушёл в тот же день: вместо него два выключателя
/// движков и две настройки охвата. Проверки на соответствие номера пункта
/// режиму сняты вместе с ним — проверять стало нечего, а держать их
/// значило бы сторожить то, чего нет.
/// </para>
/// </remarks>
public sealed class TunnelSettingsTests
{
    [Fact]
    public void The_window_is_created_without_throwing()
    {
        // Разметка разбирается, привязки существуют, настройки читаются.
        // Проверка дешёвая и ловит ровно то, что иначе всплывает нажатием
        // «Настройки» у владельца.
        Sta.Run(() =>
        {
            var window = new TunnelSettingsWindow();

            Assert.False(window.Changed);

            window.Close();
        });
    }

    [Fact]
    public void Settings_without_the_new_fields_keep_working()
    {
        // Переход: у людей записан режим и больше ничего. Прочитав пустое
        // поле как «выключено», мы сняли бы им десинк молча — при том,
        // что они ничего не просили.
        var settings = new AppSettings { Mode = OperatingMode.Selective };

        Assert.True(settings.Engines.Desync);
        Assert.True(settings.Engines.Tunnel);
    }

    [Theory]
    [InlineData(OperatingMode.Off, false, false)]
    [InlineData(OperatingMode.DesyncOnly, true, false)]
    [InlineData(OperatingMode.Selective, true, true)]
    [InlineData(OperatingMode.ProxyAll, false, true)]
    public void An_old_mode_is_read_as_the_right_switches(
        OperatingMode mode, bool desync, bool tunnel)
    {
        var settings = new AppSettings { Mode = mode };

        Assert.Equal(desync, settings.Engines.Desync);
        Assert.Equal(tunnel, settings.Engines.Tunnel);
    }

    [Fact]
    public void A_written_switch_wins_over_the_mode()
    {
        // Заполненное поле решает: с первого щелчка по выключателю режим
        // становится ведомым, а не ведущим.
        var settings = new AppSettings
        {
            Mode = OperatingMode.Selective,
            DesyncEnabled = false,
        };

        Assert.False(settings.Engines.Desync);
        Assert.True(settings.Engines.Tunnel);
    }

    [Fact]
    public void Writing_a_choice_writes_the_mode_too()
    {
        // На языке режимов говорят конфиг движка, отчёты и консоль.
        // Оставленный отставшим, режим развёл бы показания: окно говорило
        // бы одно, движок делал другое.
        var settings = new AppSettings().With(new EngineChoice
        {
            Desync = true,
            Tunnel = false,
        });

        Assert.Equal(OperatingMode.DesyncOnly, settings.Mode);
        Assert.Equal(true, settings.DesyncEnabled);
        Assert.Equal(false, settings.TunnelEnabled);
    }

    [Fact]
    public void A_choice_written_and_read_back_is_the_same()
    {
        var choice = new EngineChoice
        {
            Desync = false,
            Tunnel = true,
            IgnoreExclusions = true,
        };

        var back = new AppSettings().With(choice).Engines;

        Assert.Equal(choice.Desync, back.Desync);
        Assert.Equal(choice.Tunnel, back.Tunnel);
        Assert.Equal(choice.IgnoreExclusions, back.IgnoreExclusions);

        // Охват не хранится, а выводится: туннель без десинка забирает всё.
        Assert.True(back.TunnelTakesAll);
    }

    [Fact]
    public void The_old_strict_mode_becomes_the_setting()
    {
        // «Без исключений» — прежнее имя для «всё через туннель, игнорируя
        // и российские сети». У тех, кто им пользовался, ничего
        // не поменяется молча.
        var settings = new AppSettings { Mode = OperatingMode.ProxyStrict };

        Assert.True(settings.Engines.TunnelTakesAll);
        Assert.True(settings.Engines.IgnoreExclusions);
    }
}
