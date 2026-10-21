using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Рамки плиток видны со всех сторон: не тоньше двух пикселей и не срезаны раскладкой.
/// </summary>
/// <remarks>
/// Владелец 21.10: «границы тайлов пропадают на всех тайлах». Причин было две,
/// и тесты по одному на каждую.
/// </remarks>
public sealed class BorderThicknessTests
{
    private static readonly double[] Allowed = [0, 1.5, 2];

    /// <summary>
    /// Метка оттенка в окне выбора цвета: шириной 6, рамка 1,5 съела бы её
    /// почти целиком, а сторона, ушедшая за край, на белом не видна.
    /// </summary>
    private static readonly (string File, string Value)[] Exceptions = [("ColorPickerWindow.xaml", "1")];

    private static readonly Regex Markup = new(
        @"BorderThickness=""(?<v>[^""{]+)""|Property=""BorderThickness""\s+Value=""(?<v>[^""{]+)""",
        RegexOptions.Compiled);

    private static readonly Regex Code = new(
        @"BorderThickness\s*=\s*new Thickness\((?<v>[^)]*)\)",
        RegexOptions.Compiled);

    /// <summary>Рамка — 1,5 или 2, не тоньше.</summary>
    /// <remarks>
    /// Border при округлении раскладки ставит линию рамки на её середину,
    /// округлённую до пикселя: у рамки 1 при 100 и 125 % Windows это ноль,
    /// и линия ложится ровно на край плитки — с какой стороны, решает случай.
    /// Рамки 1,5 и 2 — два пикселя, и половина у них целая. Рамку разной
    /// толщины по сторонам (линия-разделитель <c>0,1,0,0</c>) Border рисует
    /// заливкой между двумя прямоугольниками, а не линией по середине, —
    /// она ложится внутрь и при толщине 1.
    /// </remarks>
    [Fact]
    public void NoBorderIsThinnerThanTwoPixels()
    {
        var found = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Xaml"), "*.xaml"))
            Check(file, Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline), Markup, found);

        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Code"), "*.cs.txt"))
            Check(file, File.ReadAllText(file), Code, found);

        Assert.Empty(found);
    }

    /// <summary>
    /// Ни одну рамку не срезает раскладка: у каждого раздела на странице
    /// владельца (1290) рамка видна со всех сторон.
    /// </summary>
    /// <remarks>
    /// <para>
    /// У плиток быстрых действий на «Главной» отступ кнопки был 9 сверху
    /// и снизу — при 125 % это 22,5 пикселя. Полпикселя терялись при
    /// округлении, место под начинку выходило на пиксель меньше плитки,
    /// и раскладка срезала её верхний ряд вместе с рамкой (снимок владельца
    /// 21.10). Так же у плиток «Диагностики» (12,9) и у кнопки «Начать замер»
    /// (карточка 20,14,22,16). Сумма отступов по оси, кратная 4, — целые
    /// пиксели и при 100, и при 125, и при 150 %.
    /// </para>
    /// <para>
    /// При 100 % пиксель равен точке, и ловить нечего: проверка идёт, когда
    /// экран крупнее, — у владельца 125 %, на его машине тесты гоняют все сессии.
    /// Узкое окно не проверяется: там свои переполнения (карточки DNS в две
    /// колонки при 700 срезаны на пиксель справа — звёздочки делят нечётное
    /// число пикселей).
    /// </para>
    /// </remarks>
    [Fact]
    public void NoBorderIsCutByLayout()
    {
        var found = new List<string>();

        Sta.Run(() =>
        {
            foreach (var row in ViewsLoadTests.Views)
            {
                var view = ((Func<UserControl>)row[1])();
                view.Width = 1290;
                view.Height = 794;

                var window = new Window
                {
                    SizeToContent = SizeToContent.WidthAndHeight, UseLayoutRounding = true,
                    Left = -6000, Top = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, Content = view,
                };

                window.Show();

                bool pixelIsPoint = VisualTreeHelper.GetDpi(window).PixelsPerDip == 1;

                // Разделы достраиваются после показа: плитки «Диагностики» — по итогам проверок.
                var until = DateTime.Now.AddSeconds(pixelIsPoint ? 0 : 1);
                while (DateTime.Now < until)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                if (!pixelIsPoint)
                    Cuts(view, view, (string)row[0], found);

                // Закрыть нельзя: закрытие последнего окна гасит общее приложение тестов.
                window.Hide();
            }
        });

        Assert.Empty(found);
    }

    private static void Cuts(DependencyObject node, FrameworkElement root, string view, List<string> found)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);

            if (child is Border { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, BorderBrush: not null } border
                && border.BorderThickness.Top > 0 && border.BorderThickness.Top == border.BorderThickness.Left
                && Cut(border, root) is { } cut)
            {
                found.Add($"{view}: рамка {border.ActualWidth:0.#}×{border.ActualHeight:0.#} в {Named(border)} срезана — {cut}");
            }

            Cuts(child, root, view, found);
        }
    }

    /// <summary>Чем срезана рамка: стороны и сколько, или null — цела.</summary>
    private static string? Cut(Border border, FrameworkElement root)
    {
        var rect = new Rect(0, 0, border.ActualWidth, border.ActualHeight);

        for (var up = VisualTreeHelper.GetParent(border); up is not null && up != root; up = VisualTreeHelper.GetParent(up))
        {
            // Прокрутка срезает то, что ниже края, — так и задумано; холст
            // мастера срезает свои фигуры нарочно.
            if (up is ScrollContentPresenter or Canvas)
                return null;

            if (up is not Visual visual || VisualTreeHelper.GetClip(visual) is not { } clip)
                continue;

            var at = border.TransformToAncestor(visual).TransformBounds(rect);
            var room = clip.Bounds;
            double top = room.Top - at.Top, left = room.Left - at.Left, bottom = at.Bottom - room.Bottom, right = at.Right - room.Right;

            if (top > 0.01 || left > 0.01 || bottom > 0.01 || right > 0.01)
                return $"сверху {top:0.#}, слева {left:0.#}, снизу {bottom:0.#}, справа {right:0.#}";
        }

        return null;
    }

    private static string Named(DependencyObject element)
    {
        for (var up = element; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is FrameworkElement { Name.Length: > 0 } named)
                return named.Name;
        }

        return "?";
    }

    private static void Check(string file, string text, Regex pattern, List<string> found)
    {
        var name = Path.GetFileName(file).Replace(".txt", string.Empty, StringComparison.Ordinal);

        foreach (Match match in pattern.Matches(text))
        {
            var value = match.Groups["v"].Value.Trim();

            if (Exceptions.Contains((name, value)))
                continue;

            var sides = value.Split(',', StringSplitOptions.TrimEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var side) ? side : double.NaN)
                .ToList();

            // Разная толщина по сторонам — своя отрисовка, без линии по середине.
            if (sides.Distinct().Count() > 1 && !sides.Any(double.IsNaN))
                continue;

            if (!Allowed.Contains(sides[0]))
                found.Add($"{name}: {value}");
        }
    }
}
