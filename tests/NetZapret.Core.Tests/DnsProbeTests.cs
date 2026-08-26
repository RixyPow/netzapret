using System.Reflection;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверка резолверов настоящим запросом DoH.
/// </summary>
/// <remarks>
/// Проверяется сборка запроса и разбор ответа — то, что ломается молча.
/// Сам поход в сеть здесь не делается: он зависит от того, что сейчас
/// перекрыл оператор, и в тестах давал бы то зелёное, то красное
/// безотносительно к нашему коду.
/// </remarks>
public sealed class DnsProbeTests
{
    private static byte[] BuildQuery(string name) =>
        (byte[])typeof(DnsProbe)
            .GetMethod("BuildQuery", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [name])!;

    private static bool HasAnswer(byte[] response) =>
        (bool)typeof(DnsProbe)
            .GetMethod("HasAnswer", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [response])!;

    [Fact]
    public void TheQueryLooksLikeADnsMessage()
    {
        var query = BuildQuery("example.com");

        // Заголовок: двенадцать байт, из них идентификатор нулевой —
        // так требует RFC 8484, чтобы ответы кэшировались на уровне HTTP.
        Assert.Equal(0x00, query[0]);
        Assert.Equal(0x00, query[1]);

        // Один вопрос, ноль ответов.
        Assert.Equal(1, (query[4] << 8) | query[5]);
        Assert.Equal(0, (query[6] << 8) | query[7]);

        // Имя по меткам: 7 "example" 3 "com" 0.
        Assert.Equal(7, query[12]);
        Assert.Equal("example", System.Text.Encoding.ASCII.GetString(query, 13, 7));
        Assert.Equal(3, query[20]);
        Assert.Equal("com", System.Text.Encoding.ASCII.GetString(query, 21, 3));
        Assert.Equal(0, query[24]);

        // Тип A, класс IN.
        Assert.Equal(1, (query[25] << 8) | query[26]);
        Assert.Equal(1, (query[27] << 8) | query[28]);
    }

    [Fact]
    public void ALabelLongerThanAllowedIsRejected()
    {
        // Метка длиннее 63 байт не записывается одним байтом длины: молча
        // обрезав её, мы бы отправили осмысленный, но чужой запрос.
        var tooLong = new string('a', 64);

        Assert.Throws<TargetInvocationException>(() => BuildQuery($"{tooLong}.com"));
    }

    [Fact]
    public void AnAnswerWithRecordsCounts()
    {
        var response = new byte[12];
        response[3] = 0x00; // код возврата 0
        response[6] = 0x00;
        response[7] = 0x01; // одна запись

        Assert.True(HasAnswer(response));
    }

    [Fact]
    public void AnEmptyAnswerDoesNotCount()
    {
        // Сервер, вернувший ноль записей, имя не разрешил — чем бы он
        // ни ответил на уровне HTTP.
        var response = new byte[12];

        Assert.False(HasAnswer(response));
    }

    [Fact]
    public void AnErrorCodeDoesNotCount()
    {
        var response = new byte[12];
        response[3] = 0x03; // NXDOMAIN
        response[7] = 0x01;

        Assert.False(HasAnswer(response));
    }

    [Fact]
    public void ATruncatedResponseDoesNotCount()
    {
        Assert.False(HasAnswer(new byte[5]));
        Assert.False(HasAnswer([]));
    }

    [Fact]
    public void EveryKnownResolverIsAnAddressNotAName()
    {
        // Имя потребовало бы резолвера для себя самого — того самого,
        // который мы и проверяем.
        Assert.All(DnsProbe.Known, r =>
        {
            Assert.True(System.Net.IPAddress.TryParse(r.Address, out _), $"{r.Name}: {r.Address}");
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
        });
    }

    [Fact]
    public void ResolverAddressesAreUnique()
    {
        var addresses = DnsProbe.Known.Select(r => r.Address).ToList();

        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.Ordinal).Count());
    }
}
