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
/// Охват туннеля сперва был третьим выключателем и оказался лишним —
/// поправка владельца в тот же день. Он выводится: туннель без десинка
/// забирает всё, вместе с десинком оставляет тому работу.
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
        Assert.True(choice.IgnoreExclusions);

        // С 23.09 — обратно в тот же режим: настройка снова значит «всё
        // в туннель», как значил он.
        Assert.Equal(OperatingMode.ProxyStrict, choice.Mode);
    }

    [Fact]
    public void The_tunnel_alone_takes_everything()
    {
        // Чинить имена больше нечем, и выпускать что-либо напрямую незачем.
        var alone = new EngineChoice { Desync = false, Tunnel = true };

        Assert.True(alone.TunnelTakesAll);
    }

    [Fact]
    public void With_desync_it_leaves_work_to_it()
    {
        // Иначе winws2 крутится вхолостую: TUN забирает весь трафик,
        // и до WinDivert не доходит ничего.
        var both = new EngineChoice { Desync = true, Tunnel = true };

        Assert.False(both.TunnelTakesAll);
    }

    [Fact]
    public void The_bad_combination_cannot_be_assembled_at_all()
    {
        // Ради этого охват и перестал быть настройкой. Отдельный
        // выключатель добавлял ровно одно сочетание — оба движка и туннель,
        // забравший всё, — то самое, на которое приходилось жаловаться.
        foreach (var desync in new[] { true, false })
        {
            foreach (var tunnel in new[] { true, false })
            {
                var choice = new EngineChoice { Desync = desync, Tunnel = tunnel };

                Assert.False(choice.DesyncRuns && choice.TunnelTakesAll);
            }
        }
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
    public void Ordinary_combinations_have_nothing_to_complain_about()
    {
        // Жалоба на каждое сочетание перестала бы читаться, и настоящую
        // среди них не заметили бы.
        Assert.Null(new EngineChoice { Desync = true, Tunnel = true }.Complaint);
        Assert.Null(new EngineChoice { Desync = true, Tunnel = false }.Complaint);
        Assert.Null(new EngineChoice { Desync = false, Tunnel = true }.Complaint);
    }

    [Fact]
    public void Sending_the_domestic_networks_abroad_warns_about_the_price()
    {
        // Банки и госуслуги через зарубежный адрес требуют подтверждений,
        // часть отказывает вовсе. Человек вправе знать это до, а не после.
        var choice = new EngineChoice
        {
            Desync = false,
            Tunnel = true,
            IgnoreExclusions = true,
        };

        Assert.Contains("банки и госуслуги", choice.Complaint);
    }

    [Fact]
    public void Ignoring_the_exclusions_with_both_switches_sends_everything_to_the_tunnel()
    {
        // Таблица владельца 23.09: «напрямую — VPN, десинк — VPN, VPN — VPN».
        // Десинку чинить нечего, и он не поднимается, хоть выключатель и стоит.
        var choice = new EngineChoice { Desync = true, Tunnel = true, IgnoreExclusions = true };

        Assert.Equal(OperatingMode.ProxyStrict, choice.Mode);
        Assert.True(choice.TunnelTakesAll);
        Assert.False(choice.DesyncRuns);
        Assert.Contains("не поднимется", choice.Complaint);
    }

    [Fact]
    public void Without_the_tunnel_the_setting_changes_nothing()
    {
        // Везти некуда: при одном десинке настройка молчит.
        var choice = new EngineChoice { Desync = true, Tunnel = false, IgnoreExclusions = true };

        Assert.Equal(OperatingMode.DesyncOnly, choice.Mode);
        Assert.True(choice.DesyncRuns);
        Assert.Null(choice.Complaint);
    }

    [Theory]
    [InlineData(true, true, "десинк и VPN по маршрутам")]
    [InlineData(true, false, "только десинк")]
    [InlineData(false, true, "только VPN, весь трафик")]
    [InlineData(false, false, "ничего не поднято")]
    public void It_says_what_it_is(bool desync, bool tunnel, string said)
    {
        var choice = new EngineChoice { Desync = desync, Tunnel = tunnel };

        Assert.Equal(said, choice.Describe());
    }

    [Fact]
    public void Every_combination_says_something()
    {
        // Четыре сочетания, и ни одно не должно остаться без имени:
        // безымянное состояние в окне выглядит поломкой.
        foreach (var desync in new[] { true, false })
        {
            foreach (var tunnel in new[] { true, false })
            {
                var choice = new EngineChoice { Desync = desync, Tunnel = tunnel };

                Assert.False(string.IsNullOrWhiteSpace(choice.Describe()));
            }
        }
    }
}
