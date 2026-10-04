using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Правый щелчок по тексту — «Копировать» (просьба пользователя 04.10: «скопировать домен из проверки»).
/// </summary>
public sealed class TextCopyTests
{
    private static T? Find<T>(DependencyObject root, Func<T, bool> wanted) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T hit && wanted(hit))
                return hit;

            if (Find(child, wanted) is { } deeper)
                return deeper;
        }

        return null;
    }

    private static Window Show(UIElement content)
    {
        var window = new Window
        {
            Left = -6000, Width = 500, Height = 300, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = content,
        };

        window.Show();
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
        return window;
    }

    private static string[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray();

    /// <summary>Строка списка: имя целиком, даже обрезанное многоточием, и вся строка.</summary>
    [Fact]
    public void A_row_offers_the_name_and_the_whole_row()
    {
        Sta.Run(() =>
        {
            // Строка — сам элемент списка: он же и контейнер строки.
            var host = new TextBlock { Text = "accounts.google.com", Width = 40, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(host);
            row.Children.Add(new TextBlock { Text = "доступен" });

            var list = new ItemsControl();
            list.Items.Add(row);

            var window = Show(list);

            var menu = TextCopy.MenuFor(host)!;

            Assert.Equal(["Копировать «accounts.google.com»", "Копировать строку целиком"], Headers(menu));

            window.Close();
        });
    }

    /// <summary>Где меню своё (серверы на вкладке VPN) — открывается оно, а не наше.</summary>
    [Fact]
    public void An_own_menu_wins()
    {
        Sta.Run(() =>
        {
            var text = new TextBlock { Text = "Эстония" };
            var holder = new Border { Child = text, ContextMenu = new ContextMenu() };

            var window = Show(holder);

            Assert.Null(TextCopy.MenuFor(text));

            window.Close();
        });
    }

    /// <summary>Значок шрифта значков копировать незачем.</summary>
    [Fact]
    public void Glyphs_are_skipped()
    {
        Sta.Run(() =>
        {
            var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons") };
            var window = Show(glyph);

            Assert.Null(TextCopy.MenuFor(glyph));

            window.Close();
        });
    }
}
