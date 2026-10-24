using NetZapret.Core;
using NetZapret.Subscriptions;

namespace NetZapret.Gui;

/// <summary>
/// Подписки, с которыми движки подняты из запаса, — спросить их панели после запуска.
/// </summary>
/// <remarks>
/// <para>
/// Пара к <see cref="SubscriptionPool.StartFromReserveFor"/>: запуск берёт
/// запас моложе суток, не дожидаясь панели, а здесь панель всё-таки
/// спрашивается — иначе запас никогда бы не обновился, а квота, срок и
/// перенос подписки (<c>new-url</c>) узнавались бы только при отказе запаса.
/// </para>
/// <para>
/// Сменился список — перезапуск предлагается, а не делается: так же, как
/// перечитывание без живых выходов (<c>SubscriptionRefreshNotice</c>). Сам
/// перезапуск роняет туннель на секунды, и решать это человеку. Следующий
/// запуск возьмёт уже новый запас — он моложе получаса и читается без сети.
/// </para>
/// <para>
/// Сети нет — панели спрашиваются, когда она появится (24.10). Автозапуск при
/// входе идёт раньше, чем подключается Wi-Fi: у владельца 23 и 24.10 — на 3–4
/// минуты, и перечитывание сразу отказывало обеим панелям, а повторить его
/// было некому, кроме перезапуска руками.
/// </para>
/// </remarks>
internal static class SubscriptionRecheck
{
    /// <summary>Идёт ли уже: два запуска подряд не должны дважды стучаться в одну панель.</summary>
    private static int _running;

    /// <summary>Сколько ждать сети: дольше — человек, видно, работает без неё.</summary>
    private static readonly TimeSpan NetworkWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Адаптер поднялся — ещё не значит, что есть DNS: у владельца 24.10 Wi-Fi
    /// подключился в 11:30:40, а «есть интернет» Windows сказала в 11:30:43.
    /// </summary>
    private static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(IReadOnlyList<PoolPart> deferred)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;

        try
        {
            if (!PhysicalNetwork.Up())
            {
                Journal.Write("подписка", "сети нет — панели подписок спрошу, когда компьютер подключится");

                if (!await PhysicalNetwork.WaitAsync(NetworkWait, CancellationToken.None))
                {
                    Journal.Write("подписка", $"сеть не появилась за {NetworkWait.TotalMinutes:0} мин — панели не спрошены, "
                        + "серверы остаются из запаса");
                    return;
                }

                await Task.Delay(NetworkSettle);
            }

            bool changed = false;

            // По одной: панель у подписок часто одна, а залпа она не любит.
            foreach (var part in deferred)
            {
                SubscriptionRead read;

                try
                {
                    read = await SubscriptionPool.ReadOneAsync(part.Source.Url, force: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Без текста ошибки: в нём могла бы оказаться ссылка.
                    Journal.Write("подписка", $"«{part.Source.Name}»: после запуска не перечиталась — {ex.GetType().Name}");
                    continue;
                }

                if (read.Source != SubscriptionReadSource.Panel || read.Info is not { Servers.Count: > 0 } info)
                {
                    Journal.Write("подписка", $"«{part.Source.Name}» не ответила и после запуска — "
                        + $"серверы остаются из запаса: {read.Error ?? "серверов нет"}");
                    continue;
                }

                SubscriptionBook.Follow(SubscriptionBook.Load(), part.Source.Url, info.NewUrl);
                SubscriptionNotices.RemindExpiry(part.Source.Name, part.Source.Url, info);

                // Сравнивается с тем, что ушло в движок: с запасом, а не с пулом —
                // у пула свои теги.
                if (SubscriptionPool.SameServers(part.Info?.Servers ?? [], info.Servers))
                {
                    Journal.Write("подписка", $"«{part.Source.Name}» перечитана после запуска — серверы те же");
                    continue;
                }

                changed = true;
                Journal.Write("подписка", $"«{part.Source.Name}» перечитана после запуска — серверы сменились, "
                    + "новые встанут с перезапуском движков");
            }

            if (changed && SupervisorHost.IsRunning())
                RestartOffer.Show("Подписка перечитана: серверы в ней сменились");
        }
        catch (Exception ex)
        {
            Journal.Write("подписка", "перечитывание после запуска сорвалось: " + ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
