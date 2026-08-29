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
    [InlineData(BlockKind.Stall)]
    [InlineData(BlockKind.Sinkhole)]
    public void Blocks_without_handshake_are_cured_by_tunnel_only(BlockKind kind)
    {
        var report = Report(kind);

        Assert.Contains("VPN", report.Remedy());
        Assert.DoesNotContain("десинк", report.Remedy());
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
