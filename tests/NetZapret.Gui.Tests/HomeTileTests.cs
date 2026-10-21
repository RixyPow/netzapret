using System.Windows;
using System.Windows.Threading;
using NetZapret.Core.Themes;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Карточка с картинкой на «Главной» стоит там, где её считает миниатюра
/// редактора тем (<see cref="SideArtLayout.Tile"/>).
/// </summary>
/// <remarks>
/// Владелец 21.10: «миниатюра врёт» — у миниатюры была своя раскладка
/// («300 из 1240»), разошедшаяся с разметкой «Главной». Поменяют поля
/// или ширину колонки в разметке — тест скажет, что формулу пора поправить.
/// </remarks>
public sealed class HomeTileTests
{
    [Theory]
    [InlineData(1302, 794)]
    [InlineData(1680, 1010)]
    public void TheTileIsWhereTheEditorThinks(double width, double height)
    {
        Sta.Run(() =>
        {
            // Карточка есть только у темы с картинкой, а тема — общее поле на
            // весь прогон: закрытый раньше редактор тем возвращает встроенную
            // тёмную, она на всю ширину, и карточки нет (CI 21.10 — через раз,
            // по порядку классов тестов).
            bool wasWide = Themes.WideHome;
            Themes.WideHome = false;

            try
            {
                Check(width, height);
            }
            finally
            {
                Themes.WideHome = wasWide;
            }
        });
    }

    private static void Check(double width, double height)
    {
        var view = new StatusView { Width = width, Height = height };
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            Left = -4000, Top = 0,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            UseLayoutRounding = true,
            Content = view,
        };

        window.Show();
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        var tile = (FrameworkElement)view.FindName("ArtTile");
        var at = tile.TranslatePoint(new Point(0, 0), view);
        var expected = SideArtLayout.Tile(width, height)!.Value;

        // Раскладка округляет по пикселям экрана (при 125 % — 0,8 точки).
        Assert.InRange(at.X, expected.Left - 1, expected.Left + 1);
        Assert.InRange(at.Y, expected.Top - 1, expected.Top + 1);
        Assert.InRange(tile.ActualWidth, expected.Width - 1, expected.Width + 1);
        Assert.InRange(tile.ActualHeight, expected.Height - 1, expected.Height + 1);

        // Закрыть нельзя: закрытие последнего окна гасит общее приложение тестов.
        window.Hide();
    }
}
