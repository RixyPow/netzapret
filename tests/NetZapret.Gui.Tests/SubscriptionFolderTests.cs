using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Папка подписки на вкладке VPN перерисовывается, хотя остаётся тем же объектом.
/// </summary>
/// <remarks>
/// <para>
/// 13.10: после 0.15.3 папка показывала «читаю…» над подпиской, прочитанной
/// четверть часа назад. Список вкладки обновляется по месту, и строка
/// получает тот же объект папки заново — а привязки от этого не
/// перечитываются. Опыт со списком в окне показал это локально.
/// </para>
/// <para>
/// Здесь — то же на одной привязке, без списка: в CI окно не рисуется,
/// и список не заводил строк вовсе (тест со списком упал там 13.10 —
/// строки не нашлось).
/// </para>
/// </remarks>
public sealed class SubscriptionFolderTests
{
    [Fact]
    public void AFolderShowsWhatWasReadWithoutBeingReplaced()
    {
        Sta.Run(() =>
        {
            var row = new SubRow { Entry = new SubscriptionEntry { Name = "Своя", Url = "https://panel.example/sub" } };
            var text = new TextBlock();
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(SubRow.Detail)));
            text.DataContext = row;
            Pump();

            Assert.Equal("читаю…", text.Text);

            row.Detail = "11 серверов";
            text.DataContext = row;
            Pump();

            // Тот же объект заново ничего не меняет — отсюда и «читаю…».
            Assert.Equal("читаю…", text.Text);

            row.Refresh();
            Pump();

            Assert.Equal("11 серверов", text.Text);
        });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
