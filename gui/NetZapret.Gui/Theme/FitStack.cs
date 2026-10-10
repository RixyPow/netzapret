using System.Windows;
using System.Windows.Controls;

namespace NetZapret.Gui;

/// <summary>
/// Столбец строк, который показывает только целиком влезающие строки.
/// </summary>
/// <remarks>
/// <para>
/// Для списков, которые не должны прокручиваться: «Шумят больше всех»
/// и «Сайты» в «Наблюдении». Владелец 10.10, сборка 11: правая колонка на
/// развёрнутом окне всё ещё прокручивалась своей полосой — не влезали
/// последние строки «Сайтов». Лишняя строка теперь не рисуется вовсе,
/// а не обрезается посередине и не тянет за собой полосу прокрутки.
/// </para>
/// <para>
/// Без предела высоты (строкой под таблицей на узком окне) показывает всё.
/// </para>
/// </remarks>
public sealed class FitStack : Panel
{
    public FitStack() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        double used = 0, width = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

            if (used + child.DesiredSize.Height > availableSize.Height)
                break;

            used += child.DesiredSize.Height;
            width = Math.Max(width, child.DesiredSize.Width);
        }

        return new Size(width, used);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        bool full = false;

        foreach (UIElement child in InternalChildren)
        {
            double height = child.DesiredSize.Height;

            // Первая невлезшая строка закрывает список, даже если следующая
            // короче и влезла бы: строка после пропуска читалась бы как соседняя.
            full |= y + height > finalSize.Height + 0.5;

            // Невлезшие — за нижним краем, где их срезает ClipToBounds.
            child.Arrange(full
                ? new Rect(0, finalSize.Height, finalSize.Width, height)
                : new Rect(0, y, finalSize.Width, height));

            if (!full)
                y += height;
        }

        return finalSize;
    }
}
