using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Отказ REALITY нового Xray узнаётся по журналу sing-box и называется словами.
/// </summary>
/// <remarks>
/// Строки — из журнала 13.10 на подписке autoXRAY, с раскраской движка;
/// адреса и имена заменены.
/// </remarks>
public sealed class RealityRefusalTests : IDisposable
{
    /// <summary>RAW REALITY через автоподбор — так его увидел владелец сервера.</summary>
    private const string Raw =
        "\u001b[31mERROR\u001b[0m[0012] [\u001b[38;5;193m2387630001\u001b[0m 104ms] connection: "
        + "open connection to 203.0.113.90:443 using outbound/selector[auto]: reality verification failed";

    /// <summary>XHTTP REALITY: причина за адресом запроса транспорта.</summary>
    private const string Xhttp =
        "\u001b[31mERROR\u001b[0m[0001] [\u001b[38;5;108m1549834588\u001b[0m 109ms] connection: "
        + "open connection to www.cloudflare.com:443 using outbound/vless[proxy]: "
        + "Post \"https://srv.example.com/abcdef/\": reality verification failed";

    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(Raw)]
    [InlineData(Xhttp)]
    public void TheEngineLineIsRecognised(string line) => Assert.True(RealityRefusal.In(line));

    [Theory]
    [InlineData("connection: open connection to example.com:443 using outbound/vless[x]: EOF")]
    [InlineData("connection: open connection to example.com:443 using outbound/vless[x]: invalid public_key")]
    [InlineData("timeout: no recent network activity")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherFailuresAreNotIt(string? line) => Assert.False(RealityRefusal.In(line));

    /// <summary>
    /// Жалоба движка в «Проверке блокировок» несёт слова отказа — по ним окно
    /// и добавляет объяснение.
    /// </summary>
    [Fact]
    public void AComplaintCarriesTheRefusal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-reality-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, [Xhttp]);
        _files.Add(path);

        var complaint = Assert.Single(EngineLog.Complaints(["cloudflare.com"], path));

        Assert.True(RealityRefusal.In(complaint.Error));
    }

    [Fact]
    public void AFreshRefusalExplainsTheFailure()
    {
        var now = DateTimeOffset.Now;

        Assert.True(RealityRefusal.Recent(now - TimeSpan.FromSeconds(30), now));
    }

    /// <summary>
    /// Автоподбор мог уйти с сервера REALITY, и старый отказ объяснял бы чужую беду.
    /// </summary>
    [Fact]
    public void AnOldRefusalDoesNotExplainANewFailure()
    {
        var now = DateTimeOffset.Now;

        Assert.False(RealityRefusal.Recent(now - RealityRefusal.Fresh - TimeSpan.FromSeconds(1), now));
        Assert.False(RealityRefusal.Recent(null, now));
    }

    /// <summary>
    /// Те же слова sing-box пишет и при устаревшем ключе — объяснение называет
    /// обе причины и не уверяет, что подписка ни при чём.
    /// </summary>
    [Fact]
    public void TheExplanationNamesBothCausesAndTheWayOut()
    {
        Assert.Contains("26.7.28", RealityRefusal.Explanation);
        Assert.Contains("26.6.27", RealityRefusal.Explanation);
        Assert.Contains("TLS", RealityRefusal.Explanation);
        Assert.Contains("ключ в подписке устарел", RealityRefusal.Explanation);
        Assert.Contains("Обновите подписку", RealityRefusal.Explanation);
        Assert.DoesNotContain("ни при чём", RealityRefusal.Explanation);
    }
}
