using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Общее имя ECH читается из записи HTTPS: с ним браузер идёт к сайту,
/// закрытому для пробы по настоящему имени.
/// </summary>
public sealed class EchRecordTests
{
    /// <summary>Запись rutracker.org от 1.1.1.1, 20.10.</summary>
    private const string Rutracker =
        "1 . alpn=h3,h2 ipv4hint=104.21.32.39,172.67.182.196 "
        + "ech=AEX+DQBBTAAgACBJ6sYVajf+pqLQvBHqreq6Em5ENZYhK6XVL9Pqn02KagAEAAEAAQASY2xvdWRmbGFyZS1lY2guY29tAAA= "
        + "ipv6hint=2606:4700:3035::6815:2027,2606:4700:3037::ac43:b6c4";

    [Fact]
    public void ThePublicNameComesOutOfTheRecord()
    {
        Assert.Equal("cloudflare-ech.com", EchRecord.PublicName(Rutracker));
    }

    [Theory]
    [InlineData("1 . alpn=h3,h2 ipv4hint=104.21.32.39")]
    [InlineData("1 . ech=AAAA")]
    [InlineData("1 . ech=не-base64")]
    [InlineData("")]
    public void ARecordWithoutUsableEchGivesNothing(string record)
    {
        Assert.Null(EchRecord.PublicName(record));
    }

    /// <summary>Конфигурация чужой версии пропускается, знакомая за ней читается.</summary>
    [Fact]
    public void AnUnknownVersionIsSkipped()
    {
        var known = Convert.FromBase64String("AEX+DQBBTAAgACBJ6sYVajf+pqLQvBHqreq6Em5ENZYhK6XVL9Pqn02KagAEAAEAAQASY2xvdWRmbGFyZS1lY2guY29tAAA=");
        var config = known[2..];

        // Список: чужая версия 0xfe0a с телом в три байта, затем знакомая.
        byte[] foreign = [0xfe, 0x0a, 0x00, 0x03, 1, 2, 3];
        var list = new byte[2 + foreign.Length + config.Length];
        int total = foreign.Length + config.Length;

        list[0] = (byte)(total >> 8);
        list[1] = (byte)total;
        foreign.CopyTo(list, 2);
        config.CopyTo(list, 2 + foreign.Length);

        Assert.Equal("cloudflare-ech.com", EchRecord.PublicNameOf(list));
    }

    [Fact]
    public void ATruncatedListGivesNothing()
    {
        var known = Convert.FromBase64String("AEX+DQBBTAAgACBJ6sYVajf+pqLQvBHqreq6Em5ENZYhK6XVL9Pqn02KagAEAAEAAQASY2xvdWRmbGFyZS1lY2guY29tAAA=");

        Assert.Null(EchRecord.PublicNameOf(known[..40]));
        Assert.Null(EchRecord.PublicNameOf([]));
    }
}
