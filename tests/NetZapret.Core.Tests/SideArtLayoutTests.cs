using NetZapret.Core.Themes;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Картинка темы на «Главной»: как прежде и уложенная руками (владелец 15.10).</summary>
public sealed class SideArtLayoutTests
{
    // Окно владельца: страница 1240×794, колонка арта справа шириной 300.
    private const double Page = 1240, Height = 794, Centre = 1240 - 150, Aspect = 2064.0 / 1176;

    [Fact]
    public void ByDefaultItIsTheOldLayout()
    {
        var (left, top, width, height) = SideArtLayout.Fit(Page, Height, Centre, Aspect);

        // Как до 15.10: по высоте страницы, середина картинки — в середине колонки.
        Assert.Equal(Height * Aspect, width, 3);
        Assert.Equal(Height, height, 3);
        Assert.Equal(Centre - width / 2, left, 3);
        Assert.Equal(0, top, 3);
    }

    [Fact]
    public void APlacedPictureSitsWhereItWasPut()
    {
        var (left, top, width, height) = SideArtLayout.Place(Page, Height, 0.93, x: 0.86, y: 0.5, size: 0.8);

        Assert.Equal(Height * 0.8, height, 3);
        Assert.Equal(height * 0.93, width, 3);
        Assert.Equal(Page * 0.86, left + width / 2, 3);
        Assert.Equal(Height * 0.5, top + height / 2, 3);
    }

    [Fact]
    public void PlacingIsTheSameOnAnyWindowSize()
    {
        // Доли страницы: на окне вдвое больше картинка вдвое больше и там же.
        var small = SideArtLayout.Place(Page, Height, Aspect, 0.7, 0.4, 1.2);
        var large = SideArtLayout.Place(Page * 2, Height * 2, Aspect, 0.7, 0.4, 1.2);

        Assert.Equal(small.Left * 2, large.Left, 3);
        Assert.Equal(small.Top * 2, large.Top, 3);
        Assert.Equal(small.Width * 2, large.Width, 3);
    }

    [Fact]
    public void MeasureIsTheInverseOfPlace()
    {
        var placed = SideArtLayout.Place(Page, Height, Aspect, 0.67, 0.44, 1.2);
        var (x, y, size) = SideArtLayout.Measure(Page, Height, placed);

        Assert.Equal(0.67, x, 6);
        Assert.Equal(0.44, y, 6);
        Assert.Equal(1.2, size, 6);
    }

    [Fact]
    public void SizeIsKeptWithinBounds()
    {
        Assert.Equal(Height * SideArtLayout.MaxSize, SideArtLayout.Place(Page, Height, Aspect, 0.5, 0.5, 10).Height, 3);
        Assert.Equal(Height * SideArtLayout.MinSize, SideArtLayout.Place(Page, Height, Aspect, 0.5, 0.5, 0.01).Height, 3);
    }

    /// <summary>
    /// Карточка с картинкой — той же раскладкой, что «Главная» (21.10, «миниатюра
    /// врёт»): колонка карточек не шире 940, карточка от неё на 14, до края 32.
    /// </summary>
    [Theory]
    [InlineData(1302, 794, 922, 348, 772)]
    [InlineData(1680, 1010, 922, 726, 988)]
    public void TheTileFollowsTheHomeLayout(double width, double height, double left, double tileWidth, double tileHeight)
    {
        var tile = SideArtLayout.Tile(width, height)!.Value;

        Assert.Equal(left, tile.Left, 3);
        Assert.Equal(12, tile.Top, 3);
        Assert.Equal(tileWidth, tile.Width, 3);
        Assert.Equal(tileHeight, tile.Height, 3);
    }

    [Fact]
    public void ANarrowPageHasNoTile() => Assert.Null(SideArtLayout.Tile(817, 674));
}
