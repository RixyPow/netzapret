using System.Net;
using NetZapret.Etw;
using Xunit;

namespace NetZapret.Core.Tests;

public class DnsNameCacheTests
{
    [Fact]
    public void ParseQueryResultsExtractsAddressesAndSkipsCnames()
    {
        // Реальная форма поля QueryResults: CNAME'ы помечены префиксом type:,
        // адреса идут как есть, разделитель — точка с запятой.
        const string payload = "type:  5 www-example.l.google.com;142.250.185.78;2a00:1450:4010:c0d::64;";

        var addresses = DnsNameCache.ParseQueryResults(payload).ToList();

        Assert.Equal(2, addresses.Count);
        Assert.Contains(IPAddress.Parse("142.250.185.78"), addresses);
        Assert.Contains(IPAddress.Parse("2a00:1450:4010:c0d::64"), addresses);
    }

    [Fact]
    public void ParseQueryResultsToleratesEmptyAndMalformedInput()
    {
        Assert.Empty(DnsNameCache.ParseQueryResults(null));
        Assert.Empty(DnsNameCache.ParseQueryResults(""));
        Assert.Empty(DnsNameCache.ParseQueryResults(";;;"));
        Assert.Empty(DnsNameCache.ParseQueryResults("type:  5 only.a.cname.example;"));
    }

    [Fact]
    public void RecordedNameIsResolvableByAddress()
    {
        var cache = new DnsNameCache();
        cache.Record(IPAddress.Parse("142.250.185.78"), "www.google.com");

        Assert.Equal("www.google.com", cache.Resolve(IPAddress.Parse("142.250.185.78")));
        Assert.Null(cache.Resolve(IPAddress.Parse("1.1.1.1")));
    }

    [Fact]
    public void MappedIPv4IsStoredAsPlainIPv4()
    {
        // Ответ DNS может прийти как ::ffff:1.2.3.4, а соединение придёт
        // с обычным IPv4 — без приведения они бы не совпали.
        var cache = new DnsNameCache();
        cache.Record(IPAddress.Parse("::ffff:142.250.185.78"), "www.google.com");

        Assert.Equal("www.google.com", cache.Resolve(IPAddress.Parse("142.250.185.78")));
    }

    [Fact]
    public void TrailingDotIsStrippedFromTheName()
    {
        // DNS отдаёт полное имя с корневой точкой, а правила пишут без неё.
        var cache = new DnsNameCache();
        cache.Record(IPAddress.Parse("1.2.3.4"), "rutracker.org.");

        Assert.Equal("rutracker.org", cache.Resolve(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void ExpiredEntriesStopResolving()
    {
        var cache = new DnsNameCache(lifetime: TimeSpan.Zero);
        cache.Record(IPAddress.Parse("1.2.3.4"), "example.com");

        Assert.Null(cache.Resolve(IPAddress.Parse("1.2.3.4")));
    }
}
