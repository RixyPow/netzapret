namespace NetZapret.Core.Themes;

/// <summary>
/// Где лежит картинка темы на «Главной».
/// </summary>
/// <remarks>
/// <para>
/// Два способа. Как прежде (<see cref="Fit"/>): по высоте страницы, середина
/// картинки — посередине колонки арта справа. Персонаж не по центру кадра при
/// этом уходил под карточки (мечник, лицо из лифта — 15.10).
/// </para>
/// <para>
/// Руками (<see cref="Place"/>, владелец 15.10: «сделай миниатюру главной и дай
/// возможность разместить там фон, всё остальное заливается градиентом»):
/// середина картинки и её высота — доли страницы, выбираются на миниатюре
/// «Главной» в редакторе тем. Доли, а не точки: окно у людей от 1366 до 4K,
/// и уложенное на одном стоит так же на другом.
/// </para>
/// </remarks>
public static class SideArtLayout
{
    /// <summary>Наименьшая высота картинки — доля высоты страницы.</summary>
    public const double MinSize = 0.2;

    /// <summary>Наибольшая высота картинки — доля высоты страницы.</summary>
    public const double MaxSize = 3;

    /// <summary>Как прежде: по высоте страницы, середина — посередине колонки.</summary>
    /// <param name="centre">Где по ширине середина колонки арта (без колонки — середина страницы).</param>
    /// <param name="aspect">Ширина картинки к высоте.</param>
    public static (double Left, double Top, double Width, double Height) Fit(
        double pageWidth,
        double pageHeight,
        double centre,
        double aspect)
    {
        double width = pageHeight * aspect, height = pageHeight;

        // Уже страницы — растягиваем до её ширины: пустая полоса справа хуже обрезки.
        if (width < pageWidth)
        {
            width = pageWidth;
            height = pageWidth / aspect;
        }

        return (centre - width / 2, (pageHeight - height) / 2, width, height);
    }

    /// <summary>Руками: середина картинки и её высота — доли страницы.</summary>
    public static (double Left, double Top, double Width, double Height) Place(
        double pageWidth,
        double pageHeight,
        double aspect,
        double x,
        double y,
        double size)
    {
        double height = pageHeight * Math.Clamp(size, MinSize, MaxSize);
        double width = height * aspect;

        return (pageWidth * x - width / 2, pageHeight * y - height / 2, width, height);
    }

    /// <summary>Обратно: где середина и какой высоты картинка — долями страницы.</summary>
    public static (double X, double Y, double Size) Measure(
        double pageWidth,
        double pageHeight,
        (double Left, double Top, double Width, double Height) placed) =>
        ((placed.Left + placed.Width / 2) / pageWidth,
         (placed.Top + placed.Height / 2) / pageHeight,
         placed.Height / pageHeight);
}
