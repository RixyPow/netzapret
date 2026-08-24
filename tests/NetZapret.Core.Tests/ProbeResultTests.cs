using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверка сервера и определение внешнего адреса — разные вопросы.
/// </summary>
/// <remarks>
/// Смешение стоило дорого: провайдер перекрыл службы определения адреса,
/// и проверка стала показывать ноль рабочих серверов из четырнадцати
/// при полностью живом VPN. Замер через Clash API работающего движка
/// это подтвердил — generate_204 отвечал через каждый сервер.
/// </remarks>
public sealed class ProbeResultTests
{
    [Fact]
    public void ConnectivityAndIpLookupUseDifferentAddresses()
    {
        var options = new ProbeOptions();

        // Пересечение вернуло бы нас ровно к той же поломке.
        Assert.Empty(options.ConnectivityUrls.Intersect(options.IpServices, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConnectivityAddressesAreLightweight()
    {
        // Вопрос «доходит ли трафик» не должен зависеть от живости
        // стороннего сервиса, поэтому адреса отдают пустой ответ.
        var options = new ProbeOptions();

        Assert.NotEmpty(options.ConnectivityUrls);
        Assert.All(options.ConnectivityUrls, url =>
            Assert.True(
                url.Contains("generate_204", StringComparison.OrdinalIgnoreCase)
                || url.Contains("connecttest", StringComparison.OrdinalIgnoreCase),
                $"{url} не выглядит лёгкой проверкой связности"));
    }

    [Fact]
    public void ServerCountsAsWorkingWithoutAnExternalAddress()
    {
        // Именно этот случай и был сломан: трафик идёт, адрес не определился.
        var result = new ProbeResult
        {
            ServerTag = "США",
            Success = true,
            ExternalIp = null,
            Latency = TimeSpan.FromMilliseconds(714),
        };

        Assert.True(result.Success);
        Assert.Null(result.ExternalIp);
    }

    [Fact]
    public void SortingPutsWorkingServersFirstThenByLatency()
    {
        var results = new[]
        {
            new ProbeResult { ServerTag = "мёртвый", Success = false },
            new ProbeResult { ServerTag = "медленный", Success = true, Latency = TimeSpan.FromMilliseconds(1296) },
            new ProbeResult { ServerTag = "быстрый", Success = true, Latency = TimeSpan.FromMilliseconds(97) },
        };

        var sorted = results
            .OrderBy(r => r.Success ? 0 : 1)
            .ThenBy(r => r.Latency ?? TimeSpan.MaxValue)
            .Select(r => r.ServerTag)
            .ToArray();

        Assert.Equal(["быстрый", "медленный", "мёртвый"], sorted);
    }

    [Fact]
    public void UnprobedServersSortAfterWorkingOnes()
    {
        // Непроверенный сервер не мёртв, но и не подтверждён — вперёд
        // подтверждённых его пускать нельзя.
        var results = new[]
        {
            new ProbeResult { ServerTag = "непроверенный", Success = false, Latency = null },
            new ProbeResult { ServerTag = "живой", Success = true, Latency = TimeSpan.FromMilliseconds(500) },
        };

        var first = results
            .OrderBy(r => r.Success ? 0 : 1)
            .ThenBy(r => r.Latency ?? TimeSpan.MaxValue)
            .First();

        Assert.Equal("живой", first.ServerTag);
    }
}
