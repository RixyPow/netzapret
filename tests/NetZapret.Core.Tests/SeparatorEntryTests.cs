using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Разделители списка, которые поставщики кладут в подписку.
/// </summary>
/// <remarks>
/// <para>
/// Строки вида «🛜 Wi-FI сервера» и «📶 Обходы ниже» — заголовки групп для
/// чужого клиента, а не серверы. По виду ссылки они неотличимы от настоящих:
/// те же <c>vless://</c> и тот же набор полей, только адрес нулевой.
/// </para>
/// <para>
/// Замер 2026-09-16: две такие записи попали в группу автоподбора —
/// одиннадцать выходов из пятнадцати, — и когда выбор падал на них,
/// соединение обрывалось мгновенно: «dial tcp 0.0.0.0:1337: connectex:
/// No connection could be made». В отчёте это выглядело как «туннель
/// не доставил» у четырёх имён при работающих сайтах.
/// </para>
/// </remarks>
public sealed class SeparatorEntryTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_entry_without_an_address_is_not_an_outbound(string host)
    {
        var separator = Server(host, 1337);

        Assert.False(separator.HasDialableAddress);
        Assert.False(separator.IsUsableOutbound);
    }

    /// <summary>Нулевой порт — то же самое: соединяться некуда.</summary>
    [Fact]
    public void A_zero_port_is_not_an_outbound()
    {
        Assert.False(Server("203.0.113.7", 0).HasDialableAddress);
    }

    /// <summary>Настоящие серверы отсевом не задеты.</summary>
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("95.85.227.62")]
    [InlineData("2001:db8::1")]
    [InlineData("est.example.website")]
    public void A_real_server_stays(string host)
    {
        var server = Server(host, 443);

        Assert.True(server.HasDialableAddress);
        Assert.True(server.IsUsableOutbound);
    }

    /// <summary>
    /// WARP отсев переживает.
    /// </summary>
    /// <remarks>
    /// У MASQUE адрес условный — движок выбирает узел сам и полей для него
    /// не принимает, — но он не нулевой, и отсечь запасной выход заодно
    /// с разделителями было бы тихой поломкой: список просто стал бы короче.
    /// </remarks>
    [Fact]
    public void The_spare_exit_survives()
    {
        var warp = Warp.MasqueServer();

        Assert.True(warp.HasDialableAddress);
        Assert.True(warp.IsUsableOutbound);
    }

    /// <summary>
    /// Имя не разрешается на месте: живое оно или нет, покажет замер.
    /// </summary>
    /// <remarks>
    /// Отсев отвечает на вопрос «есть ли куда звонить», а не «отвечают ли
    /// там». Смешав их, мы выбрасывали бы из списка сервер, у которого
    /// в эту минуту не отвечает DNS, — и человек не понял бы, куда он делся.
    /// </remarks>
    [Fact]
    public void A_name_is_taken_on_trust()
    {
        Assert.True(Server("this-name-does-not-resolve.invalid", 443).HasDialableAddress);
    }

    private static ProxyServer Server(string host, ushort port) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = "проверка",
        Host = host,
        Port = port,
        Credential = "00000000-0000-0000-0000-000000000000",
    };
}
