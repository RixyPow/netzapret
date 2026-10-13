using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Маршруты по папкам — с перекладкой человека поверх каталога (13.10).</summary>
public sealed class RouteFoldersTests
{
    private static readonly (string? Folder, string Key, string Item)[] Catalog =
    [
        ("YouTube", "yt-video", "Видеопоток"),
        ("YouTube", "yt-ui", "Интерфейс"),
        ("Discord", "dc-text", "Сайт и переписка"),
        (null, "own-svtv", "svtv.org"),
    ];

    private static string Show(IReadOnlyList<(string? Folder, IReadOnlyList<string> Items)> groups) =>
        string.Join(" | ", groups.Select(g => $"{g.Folder ?? "—"}: {string.Join(", ", g.Items)}"));

    [Fact]
    public void WithoutMovesItIsTheCatalog() =>
        Assert.Equal(
            "YouTube: Видеопоток, Интерфейс | Discord: Сайт и переписка | —: svtv.org",
            Show(RouteFolders.Arrange(Catalog, new Dictionary<string, string>())));

    [Fact]
    public void ARouteTakenOutStandsAloneWhereItWas() =>
        Assert.Equal(
            "—: Видеопоток | YouTube: Интерфейс | Discord: Сайт и переписка | —: svtv.org",
            Show(RouteFolders.Arrange(Catalog, new Dictionary<string, string> { ["yt-video"] = "" })));

    [Fact]
    public void ARoutePutIntoAFolderJoinsIt() =>
        Assert.Equal(
            "YouTube: Видеопоток, Интерфейс, svtv.org | Discord: Сайт и переписка",
            Show(RouteFolders.Arrange(Catalog, new Dictionary<string, string> { ["own-svtv"] = "youtube" })));

    [Fact]
    public void ANewFolderAppearsWhereItsFirstRouteWas() =>
        Assert.Equal(
            "YouTube: Видеопоток | Мои: Интерфейс, svtv.org | Discord: Сайт и переписка",
            Show(RouteFolders.Arrange(Catalog, new Dictionary<string, string> { ["yt-ui"] = "Мои", ["own-svtv"] = "Мои" })));

    [Fact]
    public void ADraggedOrderWinsAndNewcomersStayByTheirNeighbour()
    {
        // Человек поставил Discord наверх; svtv.org в сохранённом порядке нет —
        // он добавлен позже и встаёт за последним из стоявших перед ним.
        var ordered = RouteFolders.Order(Catalog, ["dc-text", "yt-video", "yt-ui"]);

        Assert.Equal(["dc-text", "yt-video", "yt-ui", "own-svtv"], ordered.Select(i => i.Key));

        ordered = RouteFolders.Order(Catalog, ["yt-ui", "yt-video"]);

        Assert.Equal(["yt-ui", "yt-video", "dc-text", "own-svtv"], ordered.Select(i => i.Key));
    }

    [Fact]
    public void AFolderMovesAsOneBlock()
    {
        string[] order = ["yt-video", "yt-ui", "dc-text", "own-svtv"];

        Assert.Equal(["dc-text", "yt-video", "yt-ui", "own-svtv"],
            RouteFolders.MoveKeys(order, ["yt-video", "yt-ui"], "dc-text", after: true));

        Assert.Equal(["own-svtv", "yt-video", "yt-ui", "dc-text"],
            RouteFolders.MoveKeys(order, ["own-svtv"], "yt-video", after: false));

        // На себя — ничего.
        Assert.Equal(order, RouteFolders.MoveKeys(order, ["yt-video", "yt-ui"], "yt-ui", after: true));
    }

    [Fact]
    public void MovingBackHomeForgetsTheMove()
    {
        var moved = RouteFolders.Move(new Dictionary<string, string>(), "yt-video", "", "YouTube");
        Assert.Equal("", moved["yt-video"]);

        moved = RouteFolders.Move(moved, "yt-video", "YouTube", "YouTube");
        Assert.Empty(moved);

        // У одиночного «домой» — вне папки.
        Assert.Empty(RouteFolders.Move(new Dictionary<string, string>(), "own-svtv", "", null));
    }
}
