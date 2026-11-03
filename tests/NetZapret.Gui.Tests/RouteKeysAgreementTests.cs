using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Ключ строки «Маршрутов» у окна и у библиотеки — один и тот же.
/// </summary>
/// <remarks>
/// Загрузка маршрута файлом (RouteBundles, 03.11) пишет перекладку в папку
/// и порядок ключами библиотеки (<see cref="RouteFolders.KeyOf"/>), а окно
/// раскладывает строки своими (<c>RouteKeys</c>). Разойдись они — привезённый
/// маршрут лёг бы мимо своей папки, и молча.
/// </remarks>
public sealed class RouteKeysAgreementTests
{
    [Theory]
    [InlineData(RouteKeys.Own, "*.example.com")]
    [InlineData(RouteKeys.Own, "config/lists/own/svtv.txt")]
    [InlineData(RouteKeys.HostList, "config/lists/own/programs/repo.exe.domains.txt")]
    [InlineData(RouteKeys.HostList, "config/lists/discord-media.txt")]
    [InlineData(RouteKeys.IpSet, "config/lists/discord-voice-net.txt")]
    [InlineData(RouteKeys.Subnet, "45.82.160.0/24")]
    public void TheLibraryKeyIsTheWindowKey(string kind, string value) =>
        Assert.Equal(RouteKeys.Make(kind, value), RouteFolders.KeyOf(RouteKeys.MatchOf(kind, value), value));
}
