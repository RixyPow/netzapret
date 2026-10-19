using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Список вкладки VPN меняется по месту, и элементы окна в нём не двоятся.
/// </summary>
/// <remarks>
/// 19.10 у владельца: свернул папку пула — окно упало в
/// VirtualizingStackPanel.OnItemsReplace. Плашка «Подписки» — элемент окна,
/// а не данные, — вставала на новое место, ещё стоя на старом.
/// </remarks>
public sealed class VpnPageTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
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

    private static ServerRow Server(string tag) => new(
        tag, "Подписка", tag, "EE", Visibility.Collapsed, null, Visibility.Collapsed,
        "Vless", "1.2.3.4:443", "100 мс", Brushes.Gray, "выбрать", true, true);

    [Fact]
    public void Folding_a_pool_folder_does_not_break_the_list()
    {
        Sta.Run(() =>
        {
            var view = new VpnView();
            var window = new Window
            {
                Width = 1000, Height = 700, Left = -4000, Top = 0,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                Content = view,
            };

            window.Show();
            window.UpdateLayout();
            Pump();

            var pool = new PoolRow
            {
                Pool = new ServerPool("p1", "Россия", ["A", "B", "C"]),
                Servers = [Server("A"), Server("B"), Server("C")],
            };

            typeof(VpnView).GetField("_poolRows", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(view, new List<PoolRow> { pool });

            var show = typeof(VpnView).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!;

            // Раскрыть, свернуть и снова — плашки подписок и подпись съезжают
            // в обе стороны.
            // Прокручено вниз, как у владельца: верх вкладки уже не нарисован.
            pool.Open = true;
            show.Invoke(view, null);
            window.UpdateLayout();
            Below<System.Windows.Controls.ScrollViewer>(view)?.ScrollToEnd();
            window.UpdateLayout();
            Pump();

            var items = (System.Collections.ObjectModel.ObservableCollection<object>)typeof(VpnView)
                .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

            // Падение в тесте не повторяется — у владельца верх списка был не
            // нарисован, — поэтому сторожим саму причину: элемент окна ни
            // в одно мгновение не стоит в списке дважды.
            int worst = 0;
            items.CollectionChanged += (_, _) => worst = Math.Max(worst,
                items.OfType<UIElement>().GroupBy(e => e).Max(g => g.Count()));

            foreach (var open in new[] { false, true, false, true, false })
            {
                pool.Open = open;
                show.Invoke(view, null);
                window.UpdateLayout();
                Pump();
            }

            Assert.Equal(1, worst);

            // Закрыть нельзя: закрытие последнего окна гасит общее приложение тестов.
            window.Hide();
        });
    }
}
