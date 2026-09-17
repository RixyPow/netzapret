using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Несостоявшаяся проба данных не считается обрывом.
/// </summary>
/// <remarks>
/// <para>
/// Проверка пережила ту перестройку, которая её и породила. Прежде проба
/// делала к одному адресу четыре соединения подряд — TCP, рукопожатие 1.2,
/// рукопожатие 1.3 и только потом передача, — и часть площадок такую очередь
/// отшивала, отдавая отказ четвёртому при живых первых трёх. Теперь
/// соединение одно, но остаться проверка должна: рукопожатие по нему уже
/// прошло, и объявлять обрыв по ответу, из которого не пришло ни байта,
/// значило бы судить о том, чего мы не измерили.
/// </para>
/// <para>
/// Замер 2026-09-16 на whatsapp.net, три захода из трёх: TCP ок, оба
/// рукопожатия ок, четвёртое падает с «Получено непредвиденное сообщение».
/// По одному то же соединение проходит двадцать раз из двадцати, а сам сайт
/// отвечает «302 Found». Мессенджер при этом работал, а отчёт звал его
/// недоставленным.
/// </para>
/// </remarks>
public sealed class UnstartedTransferTests
{
    /// <summary>
    /// Соединение не поднялось — судить по нему нечего.
    /// </summary>
    /// <remarks>
    /// Состоявшееся рукопожатие доказывает, что путь до хоста исправен.
    /// Объявлять обрыв по замеру, которого не случилось, значит обвинять
    /// сайт в нашей же торопливости.
    /// </remarks>
    [Fact]
    public void A_transfer_that_never_began_is_not_a_stall()
    {
        var unstarted = new ProbeOutcome { Ok = false, Started = false, Detail = "отказ" };

        Assert.Equal(
            BlockKind.None,
            BlockCheck.Classify(Ok(), Ok(), Ok(), unstarted));
    }

    /// <summary>
    /// Оборванный посреди ответа поток по-прежнему обрыв.
    /// </summary>
    /// <remarks>
    /// Ради этого проба данных и заводилась: steamcommunity.com проходил все
    /// прежние проверки и не открывался — рукопожатие удавалось, а поток умирал
    /// на четырнадцатой тысяче байт. Перестать это замечать значило бы
    /// выбросить то единственное, что отвечает на вопрос «работает ли сайт».
    /// </remarks>
    [Fact]
    public void A_stream_cut_mid_answer_is_still_a_stall()
    {
        var cut = new ProbeOutcome { Ok = false, Started = true, Detail = "поток замер на 14381 Б" };

        Assert.Equal(
            BlockKind.Stall,
            BlockCheck.Classify(Ok(), Ok(), Ok(), cut));
    }

    /// <summary>Через туннель различие то же самое.</summary>
    [Fact]
    public void The_same_holds_through_the_tunnel()
    {
        var unstarted = new ProbeOutcome { Ok = false, Started = false };
        var cut = new ProbeOutcome { Ok = false, Started = true };

        Assert.Equal(
            BlockKind.None,
            BlockCheck.Classify(Ok(), Ok(), Ok(), unstarted, throughTunnel: true));

        Assert.Equal(
            BlockKind.TunnelFailed,
            BlockCheck.Classify(Ok(), Ok(), Ok(), cut, throughTunnel: true));
    }

    private static ProbeOutcome Ok() => new() { Ok = true };
}
