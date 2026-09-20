using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Отчёт об адресе-заглушке не спорит сам с собой.
/// </summary>
/// <remarks>
/// <para>
/// Заглушку в отчёте называют двое: <see cref="BlockKind.Sinkhole"/> —
/// вид блокировки, и <see cref="DnsSpoofResult"/> — вердикт о подмене.
/// Пока их двое, они обязаны говорить одно и то же: строка, где вид
/// блокировки «адрес-заглушка», а подмена «не выяснено», читается как
/// признание, что проверка сама не знает, что видела.
/// </para>
/// <para>
/// Проверять это стоит отдельно, потому что пути построения отчёта
/// расходятся рано: заглушка опознаётся до всяких проб, своим коротким
/// возвратом, и поле там легко забыть. Я и забыл.
/// </para>
/// </remarks>
public sealed class SinkholeAgreementTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    public void What_the_probe_calls_a_stub_the_check_calls_a_substitution(string address)
    {
        var ip = IPAddress.Parse(address);

        Assert.True(BlockCheck.IsStub(ip));
        Assert.True(DnsSpoof.Judge("example.com", [ip], null).Spoofed);
    }

    [Theory]
    [InlineData("104.18.32.7")]
    [InlineData("8.8.8.8")]
    public void An_ordinary_address_is_neither(string address)
    {
        var ip = IPAddress.Parse(address);

        Assert.False(BlockCheck.IsStub(ip));
        Assert.False(DnsSpoof.Judge("example.com", [ip], null).Spoofed);
    }

    [Fact]
    public void A_home_address_is_a_substitution_but_not_a_stub()
    {
        // Расхождение намеренное и единственное. По 192.168.1.10 законно
        // живёт домашний сервер, поэтому проба туда достучаться пробует —
        // а вот публичного сайта там не бывает, и сказать об этом стоит.
        var ip = IPAddress.Parse("192.168.1.10");

        Assert.False(BlockCheck.IsStub(ip));
        Assert.True(DnsSpoof.Judge("example.com", [ip], null).Spoofed);
    }

    [Fact]
    public void Fakeip_of_the_tunnel_is_not_accused()
    {
        // Проксируемому имени движок выдаёт адрес из 198.18.0.0/15.
        // Объявить его подменой значило бы объявить подменой собственную
        // работу туннеля — а её в отчёте и так отмечает Tunnelled.
        foreach (var address in new[] { "198.18.0.1", "198.19.255.254" })
        {
            var ip = IPAddress.Parse(address);

            Assert.False(BlockCheck.IsStub(ip));
            Assert.False(DnsSpoof.IsSinkhole(ip));
            Assert.False(DnsSpoof.IsPrivate(ip));
        }
    }
}
