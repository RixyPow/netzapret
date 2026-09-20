using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Когда супервизор перезапускает движок, а когда нет.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по жалобе 20.09: «туннель криво работает, начал отваливаться
/// на пару секунд». Замер показал, что движок был исправен — не отвечали
/// серверы подписки, семь из девяти. Проверка трафика честно проваливалась,
/// супервизор считал это поломкой движка и гасил его: восемнадцать секунд
/// тишины, подъём в ту же мёртвую подписку, и по кругу.
/// </para>
/// <para>
/// Отсюда третий исход проверки. Снаружи мёртвый выход и сломанный движок
/// выглядят одинаково — трафик не идёт, — но лечатся противоположным,
/// и перезапуск во втором случае чистый вред.
/// </para>
/// </remarks>
public sealed class SupervisorRulesTests
{
    /// <summary>
    /// Перезапускается только сломанный движок.
    /// </summary>
    /// <remarks>
    /// Главная проверка файла. Перезапусти супервизор движок при мёртвом
    /// выходе — и человек получит ровно ту беду, ради которой всё
    /// и написано: обрывы каждые полминуты на исправном движке.
    /// </remarks>
    [Theory]
    [InlineData(ServiceCheck.Broken, true)]
    [InlineData(ServiceCheck.UpstreamDown, false)]
    [InlineData(ServiceCheck.Healthy, false)]
    public void Only_a_broken_engine_is_worth_restarting(ServiceCheck check, bool expected)
    {
        Assert.Equal(expected, SupervisorRules.CountsTowardRestart(check));
    }

    /// <summary>
    /// Мёртвый выход показывается как беда, а не замалчивается.
    /// </summary>
    /// <remarks>
    /// Не перезапускать — не значит делать вид, что всё хорошо. Трафик
    /// и вправду не идёт, и человек имеет право видеть это в окне, даже
    /// когда чинить должны не мы, а владелец подписки.
    /// </remarks>
    [Fact]
    public void A_dead_exit_is_still_shown_as_unhealthy()
    {
        Assert.Equal(ServiceHealth.Degraded, SupervisorRules.HealthFor(ServiceCheck.UpstreamDown));
    }

    /// <summary>Исправное называется исправным.</summary>
    [Fact]
    public void Healthy_is_healthy()
    {
        Assert.Equal(ServiceHealth.Healthy, SupervisorRules.HealthFor(ServiceCheck.Healthy));
    }

    /// <summary>Сломанное тоже видно в окне.</summary>
    [Fact]
    public void Broken_is_shown_as_unhealthy_too()
    {
        Assert.Equal(ServiceHealth.Degraded, SupervisorRules.HealthFor(ServiceCheck.Broken));
    }

    /// <summary>
    /// Ни один исход не остаётся без ответа.
    /// </summary>
    /// <remarks>
    /// Перечислены все члены, а не выбранные: добавить исход и забыть его
    /// разобрать — ровно та ошибка, после которой новый случай молча
    /// поедет по ветке перезапуска.
    /// </remarks>
    [Fact]
    public void Every_outcome_is_answered()
    {
        foreach (ServiceCheck check in Enum.GetValues<ServiceCheck>())
        {
            var health = SupervisorRules.HealthFor(check);

            Assert.True(
                Enum.IsDefined(health),
                $"исход {check} не получил внятного здоровья");
        }
    }
}
