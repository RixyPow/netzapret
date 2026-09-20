using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Обход туннеля, пока его выходы мертвы.
/// </summary>
/// <remarks>
/// Жалоба владельца 20.09: «при падении серверов сделать так, чтоб вся
/// остальная сеть не страдала». Движок при мёртвой подписке жив и трафик
/// принимает, а наружу тот не выходит; в режиме «всё через VPN» это вся
/// сеть целиком.
/// </remarks>
public sealed class TunnelBypassTests
{
    private static TunnelBypass Fresh() => new();

    [Fact]
    public void A_working_tunnel_is_left_alone()
    {
        var bypass = Fresh();

        for (int i = 0; i < 10; i++)
            Assert.Equal(BypassAction.Keep, bypass.Observe(tunnelWorks: true));

        Assert.False(bypass.Engaged);
    }

    [Fact]
    public void One_failed_check_is_not_enough()
    {
        // Единичный отказ случается и при исправном туннеле — сеть моргнула,
        // выход перегружен. Уводить по нему значило бы метаться мимо туннеля
        // и обратно несколько раз в час.
        var bypass = Fresh();

        Assert.Equal(BypassAction.Keep, bypass.Observe(tunnelWorks: false));
        Assert.False(bypass.Engaged);
    }

    [Fact]
    public void Three_failures_in_a_row_take_the_traffic_around()
    {
        var bypass = Fresh();

        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
        Assert.Equal(BypassAction.Engage, bypass.Observe(false));

        Assert.True(bypass.Engaged);
    }

    [Fact]
    public void A_successful_check_resets_the_count()
    {
        // Два отказа, успех, два отказа — обхода быть не должно:
        // подряд их не было.
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(true);
        bypass.Observe(false);

        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
        Assert.False(bypass.Engaged);
    }

    [Fact]
    public void A_revived_exit_brings_the_traffic_back()
    {
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(false);

        Assert.Equal(BypassAction.Release, bypass.Observe(tunnelWorks: true));
        Assert.False(bypass.Engaged);
    }

    [Fact]
    public void Staying_dead_does_not_engage_twice()
    {
        // Повторное переключение — лишний запрос к движку на каждой проверке
        // и лишняя строка в журнале о том, что и так уже сделано.
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(false);

        for (int i = 0; i < 5; i++)
            Assert.Equal(BypassAction.Keep, bypass.Observe(false));

        Assert.True(bypass.Engaged);
    }

    [Fact]
    public void The_count_stops_growing_once_engaged()
    {
        var bypass = Fresh();

        for (int i = 0; i < 20; i++)
            bypass.Observe(false);

        Assert.Equal(TunnelBypass.FailuresBeforeBypass, bypass.Failures);
    }

    [Fact]
    public void Forgetting_puts_it_back_as_it_was()
    {
        // Нужно при перезапуске движка: новый процесс поднимается с группой,
        // указывающей на автоподбор, и память о прежнем обходе заставила бы
        // считать трафик уведённым, когда он идёт в туннель.
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(false);

        bypass.Forget();

        Assert.False(bypass.Engaged);
        Assert.Equal(0, bypass.Failures);
        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
    }

    [Fact]
    public void A_release_that_the_traffic_disproves_is_undone_at_once()
    {
        // Обход снимается по ответу выхода на замер, а замер — запрос
        // на двести байт. Отвечающий на него выход трафик нести не обязан:
        // 19.09 у владельца «США (вход РФ)» отвечал за 190 мс и не довозил
        // ничего. Без этого правила выходило бы качание — снятие по замеру,
        // три проверки сети вхолостую, возврат, и по кругу.
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        Assert.Equal(BypassAction.Engage, bypass.Observe(false));

        Assert.Equal(BypassAction.Release, bypass.Observe(true));

        // Первый же провал настоящего трафика, без ожидания порога.
        Assert.Equal(BypassAction.Engage, bypass.Observe(false));
        Assert.True(bypass.Engaged);
    }

    [Fact]
    public void Traffic_that_really_goes_through_confirms_the_release()
    {
        // Подтверждённое снятие не возвращается по первому же провалу:
        // дальше действует обычный порог, иначе единичный сбой сети
        // уводил бы трафик мимо туннеля навсегда.
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(false);

        bypass.Observe(true);

        // Настоящий трафик прошёл — выход подтверждён делом.
        bypass.Observe(true);

        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
        Assert.Equal(BypassAction.Engage, bypass.Observe(false));
    }

    [Fact]
    public void Forgetting_clears_the_unconfirmed_release_too()
    {
        var bypass = Fresh();

        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(false);
        bypass.Observe(true);

        bypass.Forget();

        // Память о неподтверждённом снятии относилась к прежнему процессу.
        Assert.Equal(BypassAction.Keep, bypass.Observe(false));
    }

    [Fact]
    public void Engaging_points_at_direct_and_releasing_at_the_group()
    {
        var bypass = Fresh();

        Assert.Equal("direct", bypass.TargetFor(BypassAction.Engage, "auto-latency"));
        Assert.Equal("auto-latency", bypass.TargetFor(BypassAction.Release, "auto-latency"));
        Assert.Equal("auto-latency", bypass.TargetFor(BypassAction.Keep, "auto-latency"));
    }

    [Fact]
    public void The_threshold_matches_the_one_for_dead_servers()
    {
        // Обе проверки отвечают на один вопрос — «выход мёртв или моргнул», —
        // и разные пороги означали бы два разных ответа.
        Assert.Equal(TunnelBypass.FailuresBeforeBypass, new SingBoxOptions().DeadAfterFailures);
    }
}
