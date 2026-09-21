using NetZapret.Core.Rules;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Окно настроек туннеля.
/// </summary>
/// <remarks>
/// Заведено 21.09 по решению владельца: вкладка «VPN» разрослась, и шесть
/// карточек настроек оттесняли за нижний край список подписок, ради которого
/// на неё и заходят.
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

    [Theory]
    [InlineData(OperatingMode.Selective, 0)]
    [InlineData(OperatingMode.DesyncOnly, 1)]
    [InlineData(OperatingMode.ProxyAll, 2)]
    [InlineData(OperatingMode.ProxyStrict, 3)]
    public void The_mode_and_its_row_agree(OperatingMode mode, int index)
    {
        // Переводов два — сюда и обратно, — и разойдись они, окно показывало
        // бы один режим, а писало другой. Заметить это можно было бы только
        // по сломавшейся сети.
        Assert.Equal(index, TunnelSettingsWindow.IndexOf(mode));
        Assert.Equal(mode, TunnelSettingsWindow.ModeAt(index));
    }

    [Fact]
    public void An_unknown_row_falls_back_to_the_safe_mode()
    {
        // «Выборочно» безопаснее прочих: в туннель уходит только то, для чего
        // он нужен, и российские сервисы продолжают работать. Ошибиться
        // в сторону «всё через VPN» значило бы сломать их молча.
        Assert.Equal(OperatingMode.Selective, TunnelSettingsWindow.ModeAt(-1));
        Assert.Equal(OperatingMode.Selective, TunnelSettingsWindow.ModeAt(99));
    }

    [Fact]
    public void Every_mode_has_a_row()
    {
        // Эта проверка себя уже оправдала: при первой сборке окна я забыл
        // режим «только десинк» — тот, что поднимает winws2 без туннеля
        // и нужен, когда подписка кончилась. В списке его не было вовсе,
        // и выбрать его стало бы негде.
        //
        // «Выключено» сюда не входит намеренно: это не настройка туннеля,
        // а выключатель VPN, и живёт он на самой вкладке.
        foreach (OperatingMode mode in Enum.GetValues<OperatingMode>())
        {
            if (mode == OperatingMode.Off)
                continue;

            int index = TunnelSettingsWindow.IndexOf(mode);

            Assert.Equal(mode, TunnelSettingsWindow.ModeAt(index));
        }
    }
}
