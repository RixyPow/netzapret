using System.Windows;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// «Главная» в двух видах (11.11): переключение перестраивает раздел сразу.
/// </summary>
/// <remarks>
/// Раздел один на оба вида — части прячутся и показываются (ApplyView). Тест
/// сторожит, что переключение туда и обратно ставит каждую часть на место,
/// а не оставляет её от прежнего вида.
/// </remarks>
public sealed class HomeViewTests
{
    /// <summary>Части подробного вида; в простом их нет.</summary>
    private static readonly string[] AdvancedOnly = ["Actions", "AutostartCard", "ParamsCard", "FooterStatePanel"];

    [Fact]
    public void Switching_the_view_shows_and_hides_the_advanced_parts()
    {
        Sta.Run(() =>
        {
            var view = new StatusView(advanced: false) { Width = 1300, Height = 794 };
            var window = new Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                Left = -4000, Top = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Content = view,
            };

            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            var settings = new AppSettings();

            Check(view, advanced: false);

            // Внутри операции диспетчера, как щелчок в окне: ApplyView спрашивает
            // планировщик через await, и без контекста продолжение ушло бы в пул.
            Dispatcher.CurrentDispatcher.Invoke(() => view.ApplyView(true, settings));
            Check(view, advanced: true);

            Dispatcher.CurrentDispatcher.Invoke(() => view.ApplyView(false, settings));
            Check(view, advanced: false);

            // Закрыть нельзя: закрытие последнего окна гасит общее приложение тестов.
            window.Hide();
        });
    }

    private static void Check(StatusView view, bool advanced)
    {
        foreach (var name in AdvancedOnly)
        {
            var part = (FrameworkElement)view.FindName(name);
            Assert.True(part.Visibility == (advanced ? Visibility.Visible : Visibility.Collapsed),
                $"{name} в {(advanced ? "подробном" : "простом")} виде: {part.Visibility}");
        }

        // Ссылки в шапке — в обоих видах (вернули в простой 11.11).
        Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("HeaderLinks")).Visibility);

        // «Сервер» простого вида — по режиму (без выхода туннеля его нет);
        // в подробном его место у параметров подключения, и его нет всегда.
        if (advanced)
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("ServerCard")).Visibility);

        Assert.Equal(advanced, ((System.Windows.Controls.RadioButton)view.FindName("ViewAdvanced")).IsChecked);
    }
}
