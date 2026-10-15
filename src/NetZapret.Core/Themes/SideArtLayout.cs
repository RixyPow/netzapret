namespace NetZapret.Core.Themes;

/// <summary>
/// Где лежит картинка темы на «Главной»: какая её точка встаёт в середину колонки арта.
/// </summary>
/// <remarks>
/// <para>
/// До 15.10 в середину колонки вставала середина картинки, и персонаж,
/// стоящий не по центру кадра, уходил под карточки (мечник в одноимённой
/// теме — на 65 % ширины). Владелец: «двигать фото для главной» — точка
/// центра и увеличение выбираются в редакторе тем
/// (<see cref="ThemeBackground.FocusX"/>, <see cref="ThemeBackground.Zoom"/>).
/// </para>
/// <para>
/// Картинка вписывается по высоте страницы (уже страницы — растягивается
/// до её ширины), увеличивается и сдвигается так, чтобы точка центра стояла
/// посередине колонки. Справа и по высоте картинка доходит до края всегда:
/// пустая полоса хуже обрезки, поэтому точку у самого края она туда
/// не дотянет. Слева может не доставать — там её край растворяется
/// в подложке (StatusView.PlaceArt).
/// </para>
/// </remarks>
public static class SideArtLayout
{
    /// <summary>Наибольшее увеличение: дальше пиксели картинки уже видны и в обычном кадре.</summary>
    public const double MaxZoom = 3;

    /// <summary>Место картинки на странице.</summary>
    /// <param name="pageWidth">Ширина страницы.</param>
    /// <param name="pageHeight">Высота страницы.</param>
    /// <param name="centre">Где по ширине середина колонки арта (без колонки — середина страницы).</param>
    /// <param name="aspect">Ширина картинки к высоте.</param>
    /// <param name="focusX">Точка центра по ширине картинки, 0–1.</param>
    /// <param name="focusY">Точка центра по высоте картинки, 0–1.</param>
    /// <param name="zoom">Увеличение, 1–<see cref="MaxZoom"/>.</param>
    public static (double Left, double Top, double Width, double Height) Place(
        double pageWidth,
        double pageHeight,
        double centre,
        double aspect,
        double focusX = 0.5,
        double focusY = 0.5,
        double zoom = 1)
    {
        zoom = Math.Clamp(zoom, 1, MaxZoom);
        focusX = Math.Clamp(focusX, 0, 1);
        focusY = Math.Clamp(focusY, 0, 1);

        double width = pageHeight * aspect, height = pageHeight;

        // Уже страницы — растягиваем до её ширины: пустая полоса справа хуже обрезки.
        if (width < pageWidth)
        {
            width = pageWidth;
            height = pageWidth / aspect;
        }

        width *= zoom;
        height *= zoom;

        double left = Math.Max(centre - focusX * width, pageWidth - width);
        double top = Math.Clamp(pageHeight / 2 - focusY * height, pageHeight - height, 0);

        return (left, top, width, height);
    }
}
