using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Папка подписки на вкладке VPN перерисовывается, хотя остаётся тем же объектом.
/// </summary>
/// <remarks>
/// 13.10: после 0.15.3 папка показывала «читаю…» над подпиской, прочитанной
/// четверть часа назад. Список вкладки обновляется по месту, а замена в нём
/// тем же объектом привязки не перечитывает — это и проверено здесь первым
/// утверждением, чтобы правка не держалась на догадке.
/// </remarks>
public sealed class SubscriptionFolderTests
{
    [Fact]
    public void AFolderShowsWhatWasReadWithoutBeingReplaced()
    {
        Sta.Run(() =>
        {
            var row = new SubRow { Entry = new SubscriptionEntry { Name = "Своя", Url = "https://panel.example/sub" } };
            var items = new ObservableCollection<object> { row };
            var (window, list) = Show(items);

            try
            {
                row.Detail = "11 серверов";
                items[0] = row;
                Pump();

                // Сама замена ничего не меняет — отсюда и «читаю…».
                Assert.Equal("читаю…", Shown(list));

                row.Refresh();
                Pump();

                Assert.Equal("11 серверов", Shown(list));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (Window Window, ItemsControl List) Show(ObservableCollection<object> items)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(SubRow.Detail)));

        var list = new ItemsControl
        {
            ItemTemplate = new DataTemplate(typeof(SubRow)) { VisualTree = text },
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            ItemsSource = items,
        };

        var window = new Window { Content = list, Left = -3000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        Pump();

        return (window, list);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static string? Shown(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock text)
                return text.Text;

            if (Shown(child) is { } deeper)
                return deeper;
        }

        return null;
    }
}
