using NetZapret.Core.Themes;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Точка центра и увеличение арта на «Главной» (владелец 15.10).</summary>
public sealed class SideArtLayoutTests
{
    // Окно владельца: страница 1240×794, колонка арта справа шириной 300.
    private const double Page = 1240, Height = 794, Centre = 1240 - 150, Aspect = 2064.0 / 1176;

    [Fact]
    public void WithoutAFocusItIsTheOldLayout()
    {
        var (left, top, width, height) = SideArtLayout.Place(Page, Height, Centre, Aspect);

        // Как до 15.10: по высоте страницы, середина картинки — в середине колонки.
        Assert.Equal(Height * Aspect, width, 3);
        Assert.Equal(Height, height, 3);
        Assert.Equal(Centre - width / 2, left, 3);
        Assert.Equal(0, top, 3);
    }

    [Fact]
    public void TheFocusPointLandsInTheMiddleOfTheColumn()
    {
        // Мечник стоит на 65 % ширины кадра.
        var (left, _, width, _) = SideArtLayout.Place(Page, Height, Centre, Aspect, focusX: 0.65, zoom: 1.5);

        Assert.Equal(Centre, left + 0.65 * width, 3);
    }

    [Fact]
    public void ThePictureAlwaysReachesTheRightEdgeAndCoversTheHeight()
    {
        // Точка у самого правого края — картинка дошла до края страницы и дальше не едет.
        var (left, _, width, _) = SideArtLayout.Place(Page, Height, Centre, Aspect, focusX: 1);
        Assert.Equal(Page, left + width, 3);

        // По высоте — ни сверху, ни снизу пустоты, куда бы ни стояла точка.
        foreach (var y in new[] { 0.0, 0.5, 1.0 })
        {
            var (_, top, _, height) = SideArtLayout.Place(Page, Height, Centre, Aspect, focusY: y, zoom: 2);
            Assert.True(top <= 0 && top + height >= Height, $"y={y}: {top}..{top + height}");
        }
    }

    [Fact]
    public void ZoomIsKeptWithinBounds()
    {
        var (_, _, width, _) = SideArtLayout.Place(Page, Height, Centre, Aspect, zoom: 10);
        Assert.Equal(Height * Aspect * SideArtLayout.MaxZoom, width, 3);

        (_, _, width, _) = SideArtLayout.Place(Page, Height, Centre, Aspect, zoom: 0.2);
        Assert.Equal(Height * Aspect, width, 3);
    }
}
