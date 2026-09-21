using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Два выключателя движков вместо пяти режимов.
/// </summary>
/// <remarks>
/// <para>
/// Задумка владельца, 21.09. Пять режимов пришли по одному, каждый под свой
/// случай, и объяснить разницу между «всё через VPN» и «всё без исключений»
/// удавалось только примером. Два выключателя отвечают на вопрос, который
/// человек и задаёт: что у меня сейчас работает.
/// </para>
/// <para>
/// Главное, что тут проверяется, — переход. У людей в настройках лежит
/// режим, и прочитаться он должен так, чтобы ничего не поменялось само.
/// </para>
/// </remarks>
public sealed class EngineChoiceTests
{
    [Theory]
    [InlineData(OperatingMode.Off)]
    [InlineData(OperatingMode.DesyncOnly)]
    [InlineData(OperatingMode.Selective)]
    [InlineData(OperatingMode.ProxyAll)]
    public void A_mode_read_and_written_back_is_the_same(OperatingMode mode)
    {
        // Переход обязан быть безобидным: человек не просил менять настройку,
        // он просто обновился.
        Assert.Equal(mode, EngineChoice.FromMode(mode).Mode);
    }

    [Fact]
    public void Without_exceptions_becomes_a_setting_and_keeps_its_behaviour()
    {
        // Пятый режим — тот самый, ради которого настройка и заводилась.
        // Поведение сохраняется, меняется лишь то, чем оно записано.
        var choice = EngineChoice.FromMode(OperatingMode.ProxyStrict);

        Assert.True(choice.TunnelTakesAll);
        Assert.True(choice.IgnoreRussianExclusions);
        Assert.Equal(OperatingMode.ProxyAll, choice.Mode);
    }

    [Fact]
    public void Everything_through_the_tunnel_turns_desync_off()
    {
        // TUN забирает весь трафик, до WinDivert не доходит ничего,
        // и оставленный включённым winws2 работал бы вхолостую — а выглядел
        // бы работающим. В таблице владельца так и записано: «винвс выключен».
        Assert.False(EngineChoice.FromMode(OperatingMode.ProxyAll).Desync);
        Assert.False(EngineChoice.FromMode(OperatingMode.ProxyStrict).Desync);
    }

    [Fact]
    public void Nothing_raised_is_named_so()
    {
        var nothing = new EngineChoice { Desync = false, Tunnel = false };

        Assert.False(nothing.Anything);
        Assert.Equal(OperatingMode.Off, nothing.Mode);
        Assert.Contains("ничего не поднято", nothing.Complaint);
    }

    [Fact]
    public void Desync_running_for_nothing_is_named_so()
    {
        // Сочетание, которое выглядит двойной защитой, а на деле — лишний
        // процесс. Сказать об этом должно окно, а не человек через неделю.
        var both = new EngineChoice { Desync = true, Tunnel = true, TunnelTakesAll = true };

        Assert.Contains("вхолостую", both.Complaint);
    }

    [Fact]
    public void Ordinary_combinations_have_nothing_to_complain_about()
    {
        // Жалоба на каждое сочетание перестала бы читаться, и настоящую
        // среди них не заметили бы.
        Assert.Null(new EngineChoice { Desync = true, Tunnel = true }.Complaint);
        Assert.Null(new EngineChoice { Desync = true, Tunnel = false }.Complaint);
        Assert.Null(new EngineChoice { Desync = false, Tunnel = true }.Complaint);

        Assert.Null(new EngineChoice
        {
            Desync = false,
            Tunnel = true,
            TunnelTakesAll = true,
        }.Complaint);
    }

    [Fact]
    public void Ignoring_the_russian_networks_warns_about_the_price()
    {
        // Банки и госуслуги через зарубежный адрес требуют подтверждений,
        // часть отказывает вовсе. Человек вправе знать это до, а не после.
        var choice = new EngineChoice
        {
            Desync = false,
            Tunnel = true,
            TunnelTakesAll = true,
            IgnoreRussianExclusions = true,
        };

        Assert.Contains("банки и госуслуги", choice.Complaint);
    }

    [Theory]
    [InlineData(true, true, false, "десинк и VPN по маршрутам")]
    [InlineData(true, false, false, "только десинк")]
    [InlineData(false, true, false, "только VPN, по маршрутам")]
    [InlineData(false, true, true, "только VPN, весь трафик")]
    [InlineData(false, false, false, "ничего не поднято")]
    public void It_says_what_it_is(bool desync, bool tunnel, bool all, string said)
    {
        var choice = new EngineChoice { Desync = desync, Tunnel = tunnel, TunnelTakesAll = all };

        Assert.Equal(said, choice.Describe());
    }

    [Fact]
    public void Every_combination_says_something()
    {
        // Восемь сочетаний, и ни одно не должно остаться без имени:
        // безымянное состояние в окне выглядит поломкой.
        foreach (var desync in new[] { true, false })
        {
            foreach (var tunnel in new[] { true, false })
            {
                foreach (var all in new[] { true, false })
                {
                    var choice = new EngineChoice
                    {
                        Desync = desync,
                        Tunnel = tunnel,
                        TunnelTakesAll = all,
                    };

                    Assert.False(string.IsNullOrWhiteSpace(choice.Describe()));
                }
            }
        }
    }
}
