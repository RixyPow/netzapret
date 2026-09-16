using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Отчего вердикт таков и как это объясняется человеку.
/// </summary>
/// <remarks>
/// Отделено от постановки диагноза намеренно: там проверяется, что решено
/// верно, здесь — что решение объяснено той стадией, на которой сломалось.
/// Пользователь сказал про проверку «иногда запутывает», и оба разобранных
/// случая оказались не в диагнозе, а в показе.
/// </remarks>
public class BlockCheckReasonTests
{
    private static ProbeOutcome Ok(string? detail = null) =>
        new() { Ok = true, Detail = detail, Elapsed = TimeSpan.FromSeconds(0.1) };

    private static ProbeOutcome No(string detail, double seconds = 4) =>
        new() { Ok = false, Started = true, Detail = detail, Elapsed = TimeSpan.FromSeconds(seconds) };

    private static ProbeOutcome Rst(string detail = "соединение разорвано") =>
        new() { Ok = false, Reset = true, Started = true, Detail = detail, Elapsed = TimeSpan.FromSeconds(0.2) };

    private static ProbeOutcome Nope() => new() { Ok = false, Started = true };

    private static TargetReport Report(
        ProbeOutcome tcp,
        ProbeOutcome tls12,
        ProbeOutcome tls13,
        ProbeOutcome http,
        ProbeOutcome data) => new()
        {
            Host = "example.com",
            Tcp = tcp,
            Tls12 = tls12,
            Tls13 = tls13,
            Http = http,
            Data = data,
            Kind = BlockCheck.Classify(tcp, tls12, tls13, http, data),
        };

    /// <summary>
    /// Отказ согласования — не блокировка.
    /// </summary>
    /// <remarks>
    /// Сторона отвечает словами и быстро: так говорят площадки, отключившие
    /// TLS 1.2, и старые серверы без 1.3. Фильтр так не отвечает — он рвёт
    /// соединение либо молчит. Прежде обе ячейки показывали «нет», и человек
    /// шёл подбирать рецепт к тому, что не сломано.
    /// </remarks>
    [Fact]
    public void RefusedVersionIsNotABlock()
    {
        var unsupported = new ProbeOutcome
        {
            Ok = false,
            Unsupported = true,
            Detail = "сторона не согласовала TLS 1.2 — это её настройка, не фильтр",
            Elapsed = TimeSpan.FromSeconds(0.2),
        };

        var report = Report(Ok(), unsupported, unsupported, Nope(), Nope());

        Assert.Equal(BlockKind.Handshake, report.Kind);

        // И в перечне закрытого такому имени делать нечего.
        Assert.False(report.Actionable);
        Assert.Equal("н/д", unsupported.Describe());
    }

    /// <summary>
    /// Отказ согласования у одной версии при живой другой блокировкой тоже
    /// не является: рукопожатие состоялось, значит связь исправна.
    /// </summary>
    [Fact]
    public void OneRefusedVersionStillOpen()
    {
        var unsupported = new ProbeOutcome { Ok = false, Unsupported = true, Detail = "нет 1.2" };

        Assert.Equal(
            BlockKind.None,
            BlockCheck.Classify(Ok(), unsupported, Ok(), Nope(), Ok("1200 Б")));
    }

    /// <summary>
    /// Обрыв объясняется цифрами передачи, а не удавшимся TCP.
    /// </summary>
    /// <remarks>
    /// Прежде причина бралась первой непустой из пяти, и у имени с убитым
    /// потоком в строке оказывался рассказ про TCP — то есть про то, что как
    /// раз сработало.
    /// </remarks>
    [Fact]
    public void StallIsExplainedByTheTransfer()
    {
        var report = Report(
            Ok("162.159.128.233"),
            Ok(),
            Ok(),
            Nope(),
            No("поток замер на 14381 из 154921 Б, тишина 3 с"));

        Assert.Equal(BlockKind.Stall, report.Kind);
        Assert.Equal("поток замер на 14381 из 154921 Б, тишина 3 с", report.Why);
    }

    /// <summary>
    /// У убитого рукопожатия причина берётся у TLS, и к ней приписано время.
    /// </summary>
    /// <remarks>
    /// Полсекунды и полные четыре — разные поломки. Быстрый отказ означает
    /// ответ, четыре секунды — тишину, а тишина и есть почерк отбрасывания.
    /// </remarks>
    [Fact]
    public void TlsBlockCarriesItsTiming()
    {
        var report = Report(Ok("1.2.3.4"), Rst(), Rst(), Nope(), Nope());

        Assert.Equal(BlockKind.TlsDpi, report.Kind);
        Assert.NotNull(report.Why);
        Assert.Contains("соединение разорвано", report.Why);
        Assert.Contains("0,2 с", report.Why!.Replace('.', ','));
    }

    /// <summary>
    /// К истёкшему ожиданию время не приписывается: оно уже названо словами.
    /// </summary>
    /// <remarks>
    /// Иначе выходило «нет ответа за 4 с, 151.101.130.167:443 — всего адресов
    /// 4, 4,0 с» — одна и та же цифра дважды в одной строке.
    /// </remarks>
    [Fact]
    public void TimeoutDoesNotRepeatItsOwnTiming()
    {
        var report = Report(
            Ok("1.2.3.4"),
            No("нет ответа за 4 с, 151.101.130.167:443 — всего адресов 4"),
            No("нет ответа за 4 с, 151.101.130.167:443 — всего адресов 4"),
            Nope(),
            Nope());

        Assert.Equal(BlockKind.TlsDpi, report.Kind);
        Assert.Equal("нет ответа за 4 с, 151.101.130.167:443 — всего адресов 4", report.Why);
    }

    /// <summary>
    /// Отказ согласования остаётся отказом согласования и через туннель.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь стояло обратное, и это отменено замером. Прежнее рассуждение:
    /// доходит до нас не сайт, а выход подписки, и его отказы выглядят так же,
    /// значит через туннель отказ согласования надо звать недоставкой.
    /// </para>
    /// <para>
    /// Замер 2026-09-16 показал, что выход подписки так не выглядит. Четыре
    /// вида поведения собеседника — принял и закрыл, подождал и закрыл,
    /// оборвал с RST, молчит — дают IOException либо отмену по сроку.
    /// Отказом согласования считается AuthenticationException без сокетной
    /// причины, и ни один из четырёх его не даёт.
    /// </para>
    /// <para>
    /// Обратное верно всегда: отказ согласования — ответ, пришедший словами,
    /// и чтобы он дошёл, туннель должен был доставить и запрос, и ответ.
    /// В отчёте противоречие стояло двумя строками подряд — вердикт «туннель
    /// не доставил» и причина «сторона не согласовала TLS 1.3, это её
    /// настройка, не фильтр», — и speedtest.net с nflxvideo.net числились
    /// мёртвыми, работая.
    /// </para>
    /// </remarks>
    [Fact]
    public void RefusedVersionThroughTunnelStillBlamesTheSite()
    {
        var unsupported = new ProbeOutcome { Ok = false, Unsupported = true, Detail = "нет 1.2" };

        Assert.Equal(
            BlockKind.Handshake,
            BlockCheck.Classify(Ok(), unsupported, unsupported, Nope(), Nope()));

        Assert.Equal(
            BlockKind.Handshake,
            BlockCheck.Classify(Ok(), unsupported, unsupported, Nope(), Nope(), throughTunnel: true));
    }

    /// <summary>Гео-отказ объясняется ответом сайта.</summary>
    [Fact]
    public void GeoBlockIsExplainedByTheSite()
    {
        var refused = new ProbeOutcome
        {
            Ok = true,
            Refused = true,
            Detail = "сайт ответил 403 — связь исправна, отказывает он сам",
        };

        var report = Report(Ok(), Ok(), Ok(), Nope(), refused);

        Assert.Equal(BlockKind.GeoBlock, report.Kind);
        Assert.Equal("сайт ответил 403 — связь исправна, отказывает он сам", report.Why);
    }

    /// <summary>
    /// Закрытый маршрут объясняется тем, что сказал TCP.
    /// </summary>
    [Fact]
    public void ClosedRouteIsExplainedByTcp()
    {
        var report = Report(
            No("нет ответа за 4 с, 185.70.42.12:443"),
            Nope(),
            Nope(),
            Nope(),
            Nope());

        Assert.Equal(BlockKind.Full, report.Kind);
        Assert.Equal("нет ответа за 4 с, 185.70.42.12:443", report.Why);
    }

    /// <summary>Открытому имени объяснять нечего.</summary>
    [Fact]
    public void OpenNameHasNothingToExplain()
    {
        Assert.Null(Report(Ok(), Ok(), Ok(), Nope(), Ok("1200 Б")).Why);
    }

    /// <summary>
    /// Восьмидесятый порт решает только там, где не встал 443.
    /// </summary>
    /// <remarks>
    /// Ради этого его и перестали спрашивать в остальных случаях: колонка
    /// показывалась, не значила ничего и противоречила вердикту на вид.
    /// </remarks>
    [Fact]
    public void PortEightyDecidesOnlyWhenHttpsIsDown()
    {
        // 443 не встал, 80 отвечает — маршрут жив, закрыт порт.
        Assert.Equal(
            BlockKind.HttpsPort,
            BlockCheck.Classify(Nope(), Nope(), Nope(), Ok(), Nope()));

        // 443 не встал, и 80 молчит — закрыто всё.
        Assert.Equal(
            BlockKind.Full,
            BlockCheck.Classify(Nope(), Nope(), Nope(), Nope(), Nope()));

        // А при живом 443 его значение не меняет ничего.
        Assert.Equal(
            BlockCheck.Classify(Ok(), Ok(), Ok(), Ok(), Ok()),
            BlockCheck.Classify(Ok(), Ok(), Ok(), Nope(), Ok()));
    }
}
