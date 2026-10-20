using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// «Главная» в не развёрнутом окне владельца помещается без прокрутки.
/// </summary>
/// <remarks>
/// Дважды уходила под прокрутку: 19.10 — плитки по две в ряд и строка внизу
/// (страница 755 при видимых 649), 20.10 — шрифт Manrope, у которого строки
/// выше, чем у Bahnschrift (666 при видимых 652). Оба раза владелец увидел
/// это первым. Сторожим самым высоким из шрифтов, что выбирают в
/// «Оформлении»: Manrope едет с программой, и у владельца стоит он.
/// </remarks>
public sealed class HomeFitTests
{
    // Место «Главной» в окне 1067×712 — не развёрнутое окно владельца
    // при масштабе Windows 125 % (снимки 19.10 и 20.10).
    private const double Width = 817;
    private const double Height = 674;

    [Fact]
    public void HomeFitsTheOwnersWindowInManrope()
    {
        Sta.Run(() =>
        {
            var manrope = BundledFonts.Find("Manrope")!;
            var view = new StatusView { Width = Width, Height = Height };
            var window = new Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                Left = -4000, Top = 0,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                UseLayoutRounding = true,
                FontFamily = manrope,
                Content = view,
            };

            foreach (var key in new[] { "UiFont", "DisplayFont", "MonoFont" })
                window.Resources[key] = manrope;

            window.Show();

            // Автозапуск спрашивается у планировщика в фоне, и его строка
            // приходит позже показа — ждём, пока раздел допишет всё.
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
                Pump();

            // Плашки по случаю — новая версия, просьба о звезде, предупреждения,
            // жалоба на сочетание движков — страницу и должны раздвигать: они
            // бывают не всегда, и место под ними не держится. В тесте настройки
            // по умолчанию, и жалоба на них есть («Туннель» без подписки).
            foreach (var name in new[] { "UpdateCard", "SupportCard", "Warnings", "Problem", "EnginesLine" })
                ((FrameworkElement)view.FindName(name)).Visibility = Visibility.Collapsed;

            window.UpdateLayout();
            var scroll = Below<ScrollViewer>(view)!;

            Assert.True(scroll.ExtentHeight <= scroll.ViewportHeight,
                $"страница {scroll.ExtentHeight:F1} при видимых {scroll.ViewportHeight:F1}");

            // Закрыть нельзя: закрытие последнего окна гасит общее приложение тестов.
            window.Hide();
        });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        Thread.Sleep(20);
    }

    private static T? Below<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T found)
                return found;

            if (Below<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }
}
