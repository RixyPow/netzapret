using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверяет, из чего складывается утверждение о туннеле.
/// </summary>
/// <remarks>
/// Здесь проверяется не сеть, а право на вывод. Отчёт однажды заявил, что
/// выход туннеля в России, ни разу через туннель не сходив, — и по этому
/// заявлению был выписан совет менять исправный сервер. Ниже закреплено,
/// при каких условиях такое утверждение делать можно.
/// </remarks>
public class TunnelHealthTests
{
    /// <remarks>
    /// Диапазон fakeip по умолчанию — <c>198.18.0.0/15</c>, то есть обе сети
    /// 198.18 и 198.19. Замер живой системы дал <c>198.18.0.5</c> для
    /// whatsapp.com: имя заведено в туннель.
    /// </remarks>
    [Theory]
    [InlineData("198.18.0.5", true)]
    [InlineData("198.18.0.2", true)]
    [InlineData("198.19.255.255", true)]
    [InlineData("198.20.0.1", false)]
    [InlineData("198.17.255.255", false)]
    [InlineData("149.154.167.220", false)]
    [InlineData("8.47.69.6", false)]
    public void Fake_addresses_are_recognised(string address, bool expected)
    {
        Assert.Equal(expected, TunnelHealth.IsFakeIp(IPAddress.Parse(address)));
    }

    /// <summary>Граница подсети считается по битам, а не по целым байтам.</summary>
    /// <remarks>
    /// Маска /15 задевает предпоследний бит третьего слева байта, и наивное
    /// побайтное сравнение отнесло бы к диапазону всю сеть 198.19 либо не
    /// отнесло бы ничего. Отсюда отдельная проверка нецелых масок.
    /// </remarks>
    [Theory]
    [InlineData("10.0.0.1", "10.0.0.0/8", true)]
    [InlineData("11.0.0.1", "10.0.0.0/8", false)]
    [InlineData("192.168.1.130", "192.168.1.128/25", true)]
    [InlineData("192.168.1.127", "192.168.1.128/25", false)]
    [InlineData("1.2.3.4", "0.0.0.0/0", true)]
    public void Subnets_are_matched_by_bits(string address, string cidr, bool expected)
    {
        Assert.Equal(expected, TunnelHealth.InRange(IPAddress.Parse(address), cidr));
    }

    /// <remarks>Мусор во входных данных не должен давать ложного попадания.</remarks>
    [Theory]
    [InlineData("10.0.0.1", "не подсеть")]
    [InlineData("10.0.0.1", "10.0.0.0/33")]
    [InlineData("10.0.0.1", "10.0.0.0/-1")]
    [InlineData("10.0.0.1", "fc00::/18")]
    public void Malformed_ranges_match_nothing(string address, string cidr)
    {
        Assert.False(TunnelHealth.InRange(IPAddress.Parse(address), cidr));
    }

    /// <summary>Домашний адрес нельзя приписывать туннелю.</summary>
    /// <remarks>
    /// Та самая ошибка. Служба определения адреса разрешается в настоящий
    /// адрес, а не в fakeip: правило отправляет её напрямую, и полученная
    /// страна описывает домашнего провайдера. Прежний код объявлял по ней
    /// выход туннеля российским.
    /// </remarks>
    [Fact]
    public void A_domestic_answer_measured_outside_the_tunnel_says_nothing_about_it()
    {
        var reading = new ExitReading { Address = "81.200.24.102", Country = "RU", Tunnelled = false };

        Assert.False(reading.TunnelExitsDomestically);
    }

    [Fact]
    public void A_domestic_answer_measured_through_the_tunnel_does()
    {
        var reading = new ExitReading { Address = "81.200.24.102", Country = "RU", Tunnelled = true };

        Assert.True(reading.TunnelExitsDomestically);
    }

    [Theory]
    [InlineData("DE")]
    [InlineData("US")]
    [InlineData(null)]
    public void A_foreign_or_unknown_exit_is_not_domestic(string? country)
    {
        Assert.False(new ExitReading { Country = country, Tunnelled = true }.TunnelExitsDomestically);
    }

    /// <summary>Мёртвый туннель обесценивает вердикты, незнание — нет.</summary>
    /// <remarks>
    /// Разница принципиальна. Объявить вердикты недействительными потому,
    /// что не удалось спросить у движка, — значит замолчать исправную
    /// проверку; ровно так же нельзя и обвинять сайты, когда труба мертва.
    /// </remarks>
    [Theory]
    [InlineData(TunnelState.Dead, false)]
    [InlineData(TunnelState.Alive, true)]
    [InlineData(TunnelState.Off, true)]
    [InlineData(TunnelState.Unknown, true)]
    public void Only_a_dead_tunnel_invalidates_verdicts(TunnelState state, bool expected)
    {
        Assert.Equal(expected, TunnelHealth.VerdictsAreMeaningful(state));
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Every_state_is_named(TunnelState state)
    {
        Assert.False(string.IsNullOrWhiteSpace(TunnelHealth.Describe(state)));
    }

    public static TheoryData<TunnelState> AllStates
    {
        get
        {
            var data = new TheoryData<TunnelState>();

            foreach (TunnelState state in Enum.GetValues<TunnelState>())
                data.Add(state);

            return data;
        }
    }
}
