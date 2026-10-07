namespace NetZapret.Core.Themes;

/// <summary>
/// Чёрные полосы по краям кадра — те, что видео несёт в себе.
/// </summary>
/// <remarks>
/// <para>
/// Видео 4:3, сохранённое в кадр 16:9, приходит с чёрными полосами по бокам
/// (так fish.mp4 владельца, 07.10: полосы по 12 % ширины). Колонка арта
/// «Главной» растворяет левый край кадра в подложку, но край кадра — это
/// полоса, и белое поле за ней начиналось резкой вертикальной чертой
/// (владелец, 08.10: «градиент, чтоб гифка не обрывалась»). Полосы
/// обрезаются, и растворяется уже само изображение.
/// </para>
/// <para>
/// Полоса — столбец или строка, где почти все точки почти чёрные. С одной
/// стороны обрезается не больше 40 %: тёмный ролик целиком не должен
/// исчезнуть, его края — изображение, а не полоса.
/// </para>
/// </remarks>
public static class Letterbox
{
    /// <summary>Ярче этого (по самому яркому каналу, 0–255) точка — не полоса.</summary>
    private const int Dark = 24;

    /// <summary>Доля почти чёрных точек, с которой столбец или строка — полоса.</summary>
    private const double Share = 0.98;

    /// <summary>Больше этой доли с одной стороны не обрезается.</summary>
    private const double MaxSide = 0.4;

    /// <summary>
    /// Изображение без полос в точках: слева, сверху, ширина, высота.
    /// </summary>
    /// <param name="bgra">Точки в порядке B, G, R, A.</param>
    public static (int X, int Y, int Width, int Height) Content(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
            return (0, 0, Math.Max(0, width), Math.Max(0, height));

        bool IsDark(ReadOnlySpan<byte> pixels, int x, int y)
        {
            int i = y * stride + x * 4;
            return Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) <= Dark;
        }

        int Column(ReadOnlySpan<byte> pixels, int x)
        {
            int dark = 0;

            for (int y = 0; y < height; y++)
                dark += IsDark(pixels, x, y) ? 1 : 0;

            return dark;
        }

        int Row(ReadOnlySpan<byte> pixels, int y)
        {
            int dark = 0;

            for (int x = 0; x < width; x++)
                dark += IsDark(pixels, x, y) ? 1 : 0;

            return dark;
        }

        int maxX = (int)(width * MaxSide), maxY = (int)(height * MaxSide);

        int left = 0;
        while (left < maxX && Column(bgra, left) >= height * Share)
            left++;

        int right = 0;
        while (right < maxX && Column(bgra, width - 1 - right) >= height * Share)
            right++;

        int top = 0;
        while (top < maxY && Row(bgra, top) >= width * Share)
            top++;

        int bottom = 0;
        while (bottom < maxY && Row(bgra, height - 1 - bottom) >= width * Share)
            bottom++;

        // Упёрлись в предел — это не полоса, а тёмное изображение: не режем.
        if (left >= maxX) left = 0;
        if (right >= maxX) right = 0;
        if (top >= maxY) top = 0;
        if (bottom >= maxY) bottom = 0;

        // Край полосы в уменьшенном кадре размыт: точка внутрь, чтобы
        // от полосы не осталось тёмной нитки.
        if (left > 0) left++;
        if (right > 0) right++;
        if (top > 0) top++;
        if (bottom > 0) bottom++;

        return (left, top, Math.Max(1, width - left - right), Math.Max(1, height - top - bottom));
    }
}
