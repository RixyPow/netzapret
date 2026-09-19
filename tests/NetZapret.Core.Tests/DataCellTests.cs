using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ячейка передачи: что в ней стоит и почему именно это.
/// </summary>
/// <remarks>
/// Заведено по вопросу владельца о строке <c>whatsapp.net</c> в отчёте 17.09:
/// «ДАННЫЕ: нет» рядом с вердиктом «доступен» и без строки в разделе «ПОЧЕМУ».
/// Вердикт был верен — рукопожатие прошло, путь до хоста исправен, а из ответа
/// не пришло ни байта, — но ячейка утверждала обрыв, которого не измеряли.
/// </remarks>
public sealed class DataCellTests
{
    private static TargetReport With(ProbeOutcome data) => new()
    {
        Host = "whatsapp.net",
        Tcp = new ProbeOutcome { Ok = true },
        Tls = new ProbeOutcome { Ok = true },
        Version = "1.3",
        Http = new ProbeOutcome { Ok = false },
        Data = data,
        Kind = BlockKind.None,
    };

    /// <summary>Прошло — так и сказано.</summary>
    [Fact]
    public void A_finished_transfer_reads_ok()
    {
        Assert.Equal("ок", With(new ProbeOutcome { Ok = true, Detail = "846 Б" }).DescribeData());
    }

    /// <summary>
    /// Оборванный посреди ответа поток — «нет», и это правда.
    /// </summary>
    /// <remarks>
    /// Ради этого проба данных и заведена: <c>steamcommunity.com</c> проходил
    /// все прежние проверки и не открывался, потому что поток умирал
    /// на четырнадцатой тысяче байт.
    /// </remarks>
    [Fact]
    public void A_stream_cut_mid_answer_reads_no()
    {
        var cut = new ProbeOutcome
        {
            Ok = false,
            Started = true,
            Detail = "поток замер на 14381 Б, тишина 3 с",
        };

        Assert.Equal("нет", With(cut).DescribeData());
    }

    /// <summary>
    /// Несостоявшийся замер — «н/с», а не «нет».
    /// </summary>
    /// <remarks>
    /// Та самая строка. Ни байта не пришло, значит обрыва мы не видели:
    /// сказать «нет» означало бы обвинить сайт в том, чего не измеряли,
    /// да ещё и вразрез с собственным вердиктом в той же строке.
    /// </remarks>
    [Fact]
    public void A_transfer_that_never_began_reads_as_not_measured()
    {
        var unstarted = new ProbeOutcome
        {
            Ok = false,
            Started = false,
            Detail = "Получено непредвиденное сообщение или оно имеет неправильный формат.",
        };

        Assert.Equal("н/с", With(unstarted).DescribeData());
    }

    /// <summary>Не спрашивали — прочерк, как и было.</summary>
    /// <remarks>
    /// Так выглядит передача у имени, где не состоялось рукопожатие:
    /// качать нечего, и вопрос не задавался вовсе.
    /// </remarks>
    [Fact]
    public void A_transfer_never_asked_for_stays_a_dash()
    {
        Assert.Equal("—", With(new ProbeOutcome { Ok = false }).DescribeData());
    }

    /// <summary>
    /// Отказ сайта по стране остаётся «ок»: связь исправна.
    /// </summary>
    /// <remarks>
    /// Ответ пришёл целиком, и это успех передачи. То, что в нём написано
    /// «403», — дело вердикта, а не ячейки.
    /// </remarks>
    [Fact]
    public void A_country_refusal_still_counts_as_delivered()
    {
        var refused = new ProbeOutcome { Ok = true, Refused = true, Detail = "сайт ответил 403" };

        Assert.Equal("ок", With(refused).DescribeData());
    }

    /// <summary>
    /// Проба просит сжатие, как браузер.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Стояло <c>identity</c>, и проверка тянула то, чего браузер не тянет
    /// никогда: замер 20.09 — <c>itch.io</c> отдаёт 110 636 Б без сжатия
    /// против 20 748 со сжатием, <c>x.ai</c> 249 325 против 38 983. Второе
    /// у <c>x.ai</c> почти упирается в потолок проверки.
    /// </para>
    /// <para>
    /// Платили ложными обрывами: сто семнадцать имён подряд, лишние двести
    /// килобайт на каждое — та самая теснота, из-за которой поток замирает,
    /// а сайт объявляется сломанным.
    /// </para>
    /// <para>
    /// Проверяется по исходнику: сам запрос уходит из закрытого метода
    /// внутрь живого TLS-потока, и достать его оттуда нечем, не подняв
    /// поддельный сайт с сертификатом. Строка запроса — не поведение,
    /// а константа, и сверить её с исходником честнее, чем городить
    /// вокруг неё стенд.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_probe_asks_for_compression_like_a_browser()
    {
        var source = Source("BlockCheck.cs");

        Assert.Contains("Accept-Encoding: gzip, deflate, br", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Accept-Encoding: identity", source, StringComparison.Ordinal);
    }

    /// <summary>Исходник рядом с тестами: путь ищется вверх по дереву.</summary>
    private static string Source(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "NetZapret.Proxy", file);

            if (File.Exists(candidate))
                return File.ReadAllText(candidate, Encoding.UTF8);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"не нашёлся {file}");
    }
}
