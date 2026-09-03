using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Проверяет постановку диагноза по совокупности проб.
/// </summary>
/// <remarks>
/// Сами пробы здесь не участвуют — они ходят в сеть, и результат их зависит
/// от того, что сегодня закрыто у оператора. Проверяется чистая часть: как из
/// четырёх исходов получается вывод «чем лечить». Каждый случай ниже взят из
/// настоящего прогона, и половина — из тех, где вывод был неверен.
/// </remarks>
public class BlockCheckTests
{
    private static ProbeOutcome Ok() => new() { Ok = true };

    private static ProbeOutcome No() => new() { Ok = false, Detail = "нет ответа за 6 с" };

    private static ProbeOutcome Rst() => new() { Ok = false, Reset = true, Detail = "соединение разорвано" };

    [Fact]
    public void Working_tls_means_open()
    {
        Assert.Equal(BlockKind.None, BlockCheck.Classify(Ok(), Ok(), Ok(), Ok(), Ok()));
    }

    /// <summary>Рукопожатие само по себе больше не считается ответом.</summary>
    /// <remarks>
    /// Случай <c>steamcommunity.com</c>, ради которого проба данных и заведена:
    /// TCP встал, оба рукопожатия прошли, HTTP отвечает — а сайт не открывается,
    /// потому что поток умирает на четырнадцатой тысяче байт. Прежний
    /// классификатор называл это «доступен», и пользователь остался один
    /// на один с молчащим сайтом и бодрым отчётом.
    /// </remarks>
    [Fact]
    public void Handshake_without_data_is_a_stall()
    {
        Assert.Equal(BlockKind.Stall, BlockCheck.Classify(Ok(), Ok(), Ok(), Ok(), No()));
    }

    /// <remarks>
    /// Так выглядят <c>instagram.com</c> и <c>facebook.com</c>: старый TLS
    /// не отвечает, новый работает. Сайт открывается, лечить нечего.
    /// </remarks>
    [Fact]
    public void One_working_tls_version_is_enough()
    {
        Assert.Equal(BlockKind.None, BlockCheck.Classify(Ok(), No(), Ok(), Ok(), Ok()));
    }

    /// <remarks>Почерк <c>twitter.com</c>: TCP встал, рукопожатие оборвали.</remarks>
    [Fact]
    public void Reset_during_handshake_is_dpi()
    {
        Assert.Equal(BlockKind.TlsDpi, BlockCheck.Classify(Ok(), Rst(), Rst(), Ok(), No()));
    }

    /// <remarks>
    /// Тихое отбрасывание — <c>sndcdn.com</c>, <c>notion.so</c>. Обрыва нет,
    /// но и ответа нет, а TCP при этом жив: лечится тем же десинком.
    /// </remarks>
    [Fact]
    public void Silent_drop_after_tcp_is_dpi()
    {
        Assert.Equal(BlockKind.TlsDpi, BlockCheck.Classify(Ok(), No(), No(), Ok(), No()));
    }

    /// <remarks>
    /// Прежде отвечало «закрыт полностью» — и это противоречило соседнему
    /// столбцу, где HTTP стоял «ок». Раз 80-й порт отвечает, маршрут до хоста
    /// жив, и закрыт именно 443.
    /// </remarks>
    [Fact]
    public void Dead_443_with_live_80_is_port_block()
    {
        Assert.Equal(BlockKind.HttpsPort, BlockCheck.Classify(No(), No(), No(), Ok(), No()));
    }

    /// <remarks>Молчание по всем протоколам — <c>rutor.info</c>.</remarks>
    [Fact]
    public void Silence_everywhere_is_full_block()
    {
        Assert.Equal(BlockKind.Full, BlockCheck.Classify(No(), No(), No(), No(), No()));
    }

    /// <summary>Десинк предлагается только там, где есть во что вмешиваться.</summary>
    /// <remarks>
    /// Ошибка, которая дороже прочих: посоветовать десинк там, где рукопожатия
    /// не происходит, значит отправить человека крутить пресет впустую.
    /// </remarks>
    [Theory]
    [InlineData(BlockKind.HttpsPort)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.Sinkhole)]
    public void Blocks_without_handshake_are_cured_by_tunnel_only(BlockKind kind)
    {
        var report = Report(kind);

        Assert.Contains("VPN", report.Remedy());
        Assert.DoesNotContain("десинк", report.Remedy());
    }

    /// <summary>Обрыв потока — сначала другой рецепт, и лишь потом туннель.</summary>
    /// <remarks>
    /// Сперва здесь стояло «только VPN», и рассуждение казалось верным: секция
    /// пресета трогает лишь рукопожатие, а обрыв случается много позже.
    /// Опровергнуто замером — <c>skinsrestorer.net</c> рвался на 13 505 байтах
    /// из 154 921 и вылечился рецептом на ClientHello.
    ///
    /// Объясняется тем, что DPI разбирает соединение на рукопожатии, а убивает
    /// поток позже: сорвав опознание в начале, отменяешь и то, что должно было
    /// случиться потом. Совет «только VPN» уводил от рабочего решения.
    /// </remarks>
    [Fact]
    public void A_stall_is_worth_another_desync_recipe_before_the_tunnel()
    {
        var remedy = Report(BlockKind.Stall).Remedy();

        Assert.Contains("десинк", remedy);
        Assert.Contains("VPN", remedy);
    }

    /// <summary>Отказ резолвера лечится резолвером, а не маршрутом.</summary>
    /// <remarks>
    /// Совет должен быть один. Пока здесь стояло «VPN либо свой DNS», раздел
    /// «Стоит изменить» предлагал сменить маршрут, а итог — сменить резолвер;
    /// человеку оставалось гадать, что из двух.
    /// </remarks>
    [Fact]
    public void Resolver_failure_is_cured_by_resolver()
    {
        var remedy = Report(BlockKind.Dns).Remedy();

        Assert.Contains("DNS", remedy);
        Assert.DoesNotContain("VPN", remedy);
        Assert.DoesNotContain("десинк", remedy);
    }

    /// <remarks>
    /// <c>akamai.net</c> и <c>cloudfront.net</c> записи A не имеют и не имели:
    /// работают только их поддомены. Это свойство домена, а не блокировка,
    /// и в советах ему делать нечего.
    /// </remarks>
    [Fact]
    public void Name_without_address_is_not_actionable()
    {
        Assert.False(Report(BlockKind.NoAddress).Actionable);
        Assert.False(Report(BlockKind.None).Actionable);
    }

    [Theory]
    [InlineData(BlockKind.TlsDpi)]
    [InlineData(BlockKind.Stall)]
    [InlineData(BlockKind.HttpsPort)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.Dns)]
    [InlineData(BlockKind.Sinkhole)]
    public void Real_blocks_are_actionable(BlockKind kind)
    {
        Assert.True(Report(kind).Actionable);
    }

    /// <summary>Отказ сайта по стране — не то же, что отказ канала.</summary>
    /// <remarks>
    /// Случай ChatGPT. Десинк отрабатывал безупречно — рукопожатие за семь
    /// сотых секунды, — а Cloudflare с московского узла отвечал 403. Всё,
    /// что мы умеем мерить, говорило «доступен», и по своим меркам было право:
    /// работала связь, отказывал сайт. Десинк тут бессилен, он влияет на вид
    /// соединения для DPI, а решение принимает сайт, уже разобрав запрос.
    /// </remarks>
    [Fact]
    public void A_site_refusing_by_country_is_not_available()
    {
        var refused = new ProbeOutcome { Ok = true, Refused = true };

        Assert.Equal(BlockKind.GeoBlock, BlockCheck.Classify(Ok(), Ok(), Ok(), Ok(), refused));
        Assert.Contains("VPN", Report(BlockKind.GeoBlock).Remedy());
        Assert.Contains("десинк не поможет", Report(BlockKind.GeoBlock).Remedy());
    }

    /// <summary>Оборванный CNAME лечится подстановкой адреса, а не маршрутом.</summary>
    /// <remarks>
    /// Случай <c>tr.rbxcdn.com</c>: Roblox отдаёт по нему ссылки на превью,
    /// а имя — CNAME на <c>trns1.rbxcdn.com</c>, у которого записи A нет
    /// ни у одного резолвера. Прежде это попадало в «нет адреса — лечить
    /// нечего», хотя картинки у людей не грузились.
    /// </remarks>
    [Fact]
    public void Broken_cname_needs_an_address_not_a_route()
    {
        var remedy = Report(BlockKind.BrokenCname).Remedy();

        Assert.Contains("адрес", remedy);
        Assert.DoesNotContain("VPN", remedy);
        Assert.DoesNotContain("десинк", remedy);
        Assert.True(Report(BlockKind.BrokenCname).Actionable);
    }

    /// <summary>Ответ-заглушка узнаётся по адресу.</summary>
    /// <remarks>
    /// Случай <c>image.tmdb.org</c>: сеть доставки отшивает регион, отдавая
    /// петлю. Стучаться туда и объявлять хост закрытым — значит назвать
    /// чужой отказ своим.
    /// </remarks>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.5.5", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("95.100.176.225", false)]
    [InlineData("1.1.1.1", false)]
    public void Stub_addresses_are_recognised(string address, bool expected)
    {
        Assert.Equal(expected, BlockCheck.IsStub(System.Net.IPAddress.Parse(address)));
    }

    /// <summary>Внутри туннеля DPI не при чём.</summary>
    /// <remarks>
    /// <para>
    /// DPI видит шифрованный поток к серверу подписки и не знает, какое имя
    /// внутри. Значит рукопожатие оборвал не он, а туннель: не дотянулся,
    /// отказал по своим причинам либо вышел там, где хост закрыт.
    /// </para>
    /// <para>
    /// В живом отчёте таких строк оказалось шесть из девяти. Всем им
    /// предлагался десинк — вмешательство в рукопожатие, которого DPI
    /// не видел.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(BlockKind.TlsDpi)]
    [InlineData(BlockKind.Stall)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.HttpsPort)]
    public void Failures_inside_the_tunnel_are_not_blamed_on_dpi(BlockKind direct)
    {
        var (tcp, tls12, tls13, http, data) = Outcomes(direct);

        Assert.Equal(direct, BlockCheck.Classify(tcp, tls12, tls13, http, data));
        Assert.Equal(
            BlockKind.TunnelFailed,
            BlockCheck.Classify(tcp, tls12, tls13, http, data, throughTunnel: true));
    }

    /// <summary>Отказ сайта туннель не переименовывает.</summary>
    /// <remarks>
    /// 403 приходит от самого сайта и через туннель означает ровно то же:
    /// он отказал. Списать это на трубу значило бы потерять единственный
    /// вердикт, который честно указывает на страну.
    /// </remarks>
    [Fact]
    public void A_refusal_by_the_site_survives_the_tunnel()
    {
        var refused = new ProbeOutcome { Ok = true, Refused = true };

        Assert.Equal(
            BlockKind.GeoBlock,
            BlockCheck.Classify(Ok(), Ok(), Ok(), Ok(), refused, throughTunnel: true));
    }

    [Fact]
    public void A_working_target_stays_working_through_the_tunnel()
    {
        Assert.Equal(
            BlockKind.None,
            BlockCheck.Classify(Ok(), Ok(), Ok(), Ok(), Ok(), throughTunnel: true));
    }

    /// <summary>Совет по нему — про сервер, а не про рецепт.</summary>
    [Fact]
    public void A_tunnel_failure_points_at_the_server()
    {
        var remedy = Report(BlockKind.TunnelFailed).Remedy();

        Assert.Contains("сервер", remedy);
        Assert.DoesNotContain("DPI", Report(BlockKind.TunnelFailed).Describe());
        Assert.True(Report(BlockKind.TunnelFailed).Actionable);
    }

    /// <summary>Наборы исходов, дающие каждый вид без туннеля.</summary>
    private static (ProbeOutcome, ProbeOutcome, ProbeOutcome, ProbeOutcome, ProbeOutcome) Outcomes(BlockKind kind) =>
        kind switch
        {
            BlockKind.TlsDpi => (Ok(), Rst(), Rst(), Ok(), No()),
            BlockKind.Stall => (Ok(), Ok(), Ok(), Ok(), No()),
            BlockKind.HttpsPort => (No(), No(), No(), Ok(), No()),
            _ => (No(), No(), No(), No(), No()),
        };

    /// <summary>Конец заголовков находится по пустой строке.</summary>
    /// <remarks>
    /// Нужно, чтобы отличить «тело пришло целиком» от «поток убили». Пока
    /// длина заголовков не вычиталась, полученное завышалось на их размер,
    /// и страница объявлялась дочитанной раньше, чем приходила.
    /// </remarks>
    [Fact]
    public void The_end_of_the_headers_is_found()
    {
        var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");

        Assert.Equal(response.Length - 5, BlockCheck.HeaderLength(response, response.Length));
    }

    /// <remarks>
    /// Заголовки, не поместившиеся в первую порцию, дают ноль: полагаться
    /// на длину тела тогда нельзя, и признак завершения не применяется.
    /// </remarks>
    [Fact]
    public void Headers_split_across_reads_give_zero()
    {
        var partial = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n");

        Assert.Equal(0, BlockCheck.HeaderLength(partial, partial.Length));
    }

    /// <remarks>Пустое тело — тоже законченный ответ.</remarks>
    [Fact]
    public void A_response_without_a_body_still_ends()
    {
        var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\n\r\n");

        Assert.Equal(response.Length, BlockCheck.HeaderLength(response, response.Length));
    }

    /// <summary>У каждого исхода есть, что сказать человеку.</summary>
    /// <remarks>
    /// Перечислены все члены перечисления, а не выбранные: добавить вид
    /// блокировки и забыть его назвать — ровно та ошибка, из-за которой
    /// в отчёте появился бы пустой вердикт.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_kind_is_named(BlockKind kind)
    {
        var report = Report(kind);

        Assert.False(string.IsNullOrWhiteSpace(report.Describe()));
        Assert.False(string.IsNullOrWhiteSpace(report.Remedy()));
    }

    public static TheoryData<BlockKind> AllKinds
    {
        get
        {
            var data = new TheoryData<BlockKind>();

            foreach (BlockKind kind in Enum.GetValues<BlockKind>())
                data.Add(kind);

            return data;
        }
    }

    private static TargetReport Report(BlockKind kind) => new()
    {
        Host = "example.org",
        Tcp = Ok(),
        Tls12 = Ok(),
        Tls13 = Ok(),
        Http = Ok(),
        Data = Ok(),
        Kind = kind,
    };
}
