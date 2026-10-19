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
/// </remarks>
internal static class SubscriptionRecheck
{
    /// <summary>Идёт ли уже: два запуска подряд не должны дважды стучаться в одну панель.</summary>
    private static int _running;

    public static async Task RunAsync(IReadOnlyList<PoolPart> deferred)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;

        try
        {
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
