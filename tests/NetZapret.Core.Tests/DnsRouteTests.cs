using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Путь DNS движка: «авто» по умолчанию (владелец, 05.10), прежний выключатель не читается.
/// </summary>
public sealed class DnsRouteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nz-dns-route-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"DnsThroughTunnel\": true }")]
    [InlineData("{ \"DnsThroughTunnel\": false }")]
    public void Everyone_lands_on_auto(string json)
    {
        var path = Path.Combine(_dir, "netzapret.json");
        File.WriteAllText(path, json);

        Assert.Equal(DnsRoute.Auto, AppSettings.Load(path).DnsVia);
    }

    [Fact]
    public void A_choice_survives_saving()
    {
        var path = Path.Combine(_dir, "netzapret.json");

        (AppSettings.Fresh with { DnsVia = DnsRoute.Direct }).Save(path);

        Assert.Equal(DnsRoute.Direct, AppSettings.Load(path).DnsVia);
    }

    /// <summary>Без туннеля «авто» — напрямую: движок ради одного DNS — только по прямому выбору.</summary>
    [Theory]
    [InlineData(DnsRoute.Auto, false)]
    [InlineData(DnsRoute.Direct, false)]
    [InlineData(DnsRoute.Tunnel, true)]
    public void Only_tunnel_raises_the_DNS_engine_without_a_tunnel(DnsRoute route, bool engine)
    {
        var settings = new AppSettings { PresetName = "Universal V10", DnsVia = route }
            .With(new EngineChoice { Desync = true, Tunnel = false });

        Assert.Equal(engine, settings.NeedsDnsEngine);
    }

    [Theory]
    [InlineData("авто", DnsRoute.Auto)]
    [InlineData("напрямую", DnsRoute.Direct)]
    [InlineData("туннель", DnsRoute.Tunnel)]
    [InlineData("через туннель", DnsRoute.Tunnel)]
    [InlineData("tunnel", DnsRoute.Tunnel)]
    public void Words_from_nz_are_understood(string word, DnsRoute route) =>
        Assert.Equal(route, DnsRoutes.Parse(word));
}
