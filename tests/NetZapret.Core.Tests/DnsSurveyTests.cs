using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Обзор резолверов: разбор ответов и правило подмены.
/// </summary>
/// <remarks>
/// Сетевую часть здесь не проверить — она проверена живым прогоном 23.09
/// (nz dns): Google, Cloudflare и OpenDNS по UDP отвечали из
/// RIPN-NS5-RU-MSK и подменяли четыре имени из пяти, по DoH и DoT — сами.
/// </remarks>
public sealed class DnsSurveyTests
{
    /// <summary>Ответ с одной A и сжатым именем, как отдаёт любой резолвер.</summary>
    private static byte[] AnswerA(byte[] query, IPAddress address)
    {
        var reply = query.ToList();
        reply[2] = 0x81;
        reply[3] = 0x80;
        reply[7] = 1;

        // Имя — ссылкой на вопрос (смещение 12), тип A, класс IN, TTL, длина 4.
        reply.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
        reply.AddRange(address.GetAddressBytes());

        return [.. reply];
    }

    [Fact]
    public void A_compressed_answer_is_read()
    {
        var query = DnsWire.Query("example.com", DnsWire.TypeA);
        var answer = DnsWire.Parse(AnswerA(query, IPAddress.Parse("93.184.215.14")));

        Assert.NotNull(answer);
        Assert.Equal(0, answer!.Code);
        Assert.Equal(IPAddress.Parse("93.184.215.14"), Assert.Single(answer.Addresses));
    }

    [Fact]
    public void A_query_is_not_taken_for_an_answer()
    {
        // Без флага ответа это вопрос — например, отражённый назад.
        Assert.Null(DnsWire.Parse(DnsWire.Query("example.com", DnsWire.TypeA)));
    }

    [Fact]
    public void A_truncated_answer_does_not_throw()
    {
        var query = DnsWire.Query("example.com", DnsWire.TypeA);
        var full = AnswerA(query, IPAddress.Parse("1.2.3.4"));

        Assert.NotNull(DnsWire.Parse(full.AsSpan(0, full.Length - 3)));
    }

    [Theory]
    [InlineData("15169 | 8.8.8.0/24 | US | arin | 2000-03-30", "15169")]
    [InlineData("3267 196948 | 193.232.0.0/16 | RU | ripencc |", "3267")]
    [InlineData("garbage", null)]
    public void The_network_number_is_read(string text, string? asn)
    {
        Assert.Equal(asn, DnsSurvey.ParseCymruAsn(text));
    }

    [Theory]
    [InlineData("15169 | US | arin | 2000-03-30 | GOOGLE, US", "GOOGLE")]
    [InlineData("3267 | RU | ripencc | 1994-06-21 | RIPN-NS5-RU-MSK RIPN, RU", "RIPN-NS5-RU-MSK")]
    [InlineData("1 | 2", null)]
    public void The_network_name_is_read(string text, string? name)
    {
        Assert.Equal(name, DnsSurvey.ParseCymruName(text));
    }

    private static IReadOnlyList<IPAddress> Ips(params string[] values) =>
        values.Select(IPAddress.Parse).ToList();

    [Fact]
    public void Overlap_with_the_honest_answer_is_not_spoofing()
    {
        Assert.False(DnsSurvey.IsSpoofed(Ips("104.21.1.1", "172.67.1.1"), Ips("104.21.1.1"), new HashSet<IPAddress>()));
    }

    [Fact]
    public void A_different_node_of_a_delivery_network_is_not_spoofing()
    {
        // У сетей доставки узлов десятки: одно расхождение — не подмена.
        Assert.False(DnsSurvey.IsSpoofed(Ips("13.107.42.14"), Ips("13.107.42.15"), new HashSet<IPAddress>()));
    }

    [Fact]
    public void One_stub_for_many_names_is_spoofing()
    {
        // Заглушка оператора одна на всех закрытых.
        var stub = IPAddress.Parse("195.208.152.206");

        Assert.True(DnsSurvey.IsSpoofed([stub], Ips("104.21.1.1"), new HashSet<IPAddress> { stub }));
    }

    [Fact]
    public void An_empty_or_loopback_answer_is_spoofing()
    {
        Assert.True(DnsSurvey.IsSpoofed([], Ips("104.21.1.1"), new HashSet<IPAddress>()));
        Assert.True(DnsSurvey.IsSpoofed(Ips("127.0.0.1"), Ips("104.21.1.1"), new HashSet<IPAddress>()));
    }

    [Fact]
    public void Without_an_honest_answer_nothing_is_claimed()
    {
        Assert.False(DnsSurvey.IsSpoofed([], [], new HashSet<IPAddress>()));
    }
}
