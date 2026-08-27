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
        Assert.Equal(BlockKind.None, BlockCheck.Classify(Ok(), Ok(), Ok(), Ok()));
    }

    /// <remarks>
    /// Так выглядят <c>instagram.com</c> и <c>facebook.com</c>: старый TLS
    /// не отвечает, новый работает. Сайт открывается, лечить нечего.
    /// </remarks>
    [Fact]
    public void One_working_tls_version_is_enough()
    {
        Assert.Equal(BlockKind.None, BlockCheck.Classify(Ok(), No(), Ok(), Ok()));
    }

    /// <remarks>Почерк <c>twitter.com</c>: TCP встал, рукопожатие оборвали.</remarks>
    [Fact]
    public void Reset_during_handshake_is_dpi()
    {
        Assert.Equal(BlockKind.TlsDpi, BlockCheck.Classify(Ok(), Rst(), Rst(), Ok()));
    }

    /// <remarks>
    /// Тихое отбрасывание — <c>sndcdn.com</c>, <c>notion.so</c>. Обрыва нет,
    /// но и ответа нет, а TCP при этом жив: лечится тем же десинком.
    /// </remarks>
    [Fact]
    public void Silent_drop_after_tcp_is_dpi()
    {
        Assert.Equal(BlockKind.TlsDpi, BlockCheck.Classify(Ok(), No(), No(), Ok()));
    }

    /// <remarks>
    /// Прежде отвечало «закрыт полностью» — и это противоречило соседнему
    /// столбцу, где HTTP стоял «ок». Раз 80-й порт отвечает, маршрут до хоста
    /// жив, и закрыт именно 443.
    /// </remarks>
    [Fact]
    public void Dead_443_with_live_80_is_port_block()
    {
        Assert.Equal(BlockKind.HttpsPort, BlockCheck.Classify(No(), No(), No(), Ok()));
    }

    /// <remarks>Молчание по всем протоколам — <c>rutor.info</c>.</remarks>
    [Fact]
    public void Silence_everywhere_is_full_block()
    {
        Assert.Equal(BlockKind.Full, BlockCheck.Classify(No(), No(), No(), No()));
    }

    /// <summary>Десинк предлагается только там, где есть во что вмешиваться.</summary>
    /// <remarks>
    /// Ошибка, которая дороже прочих: посоветовать десинк там, где рукопожатия
    /// не происходит, значит отправить человека крутить пресет впустую.
    /// </remarks>
    [Theory]
    [InlineData(BlockKind.HttpsPort)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.Dns)]
    public void Blocks_without_handshake_are_cured_by_tunnel_only(BlockKind kind)
    {
        var report = Report(kind);

        Assert.Contains("VPN", report.Remedy());
        Assert.DoesNotContain("десинк", report.Remedy());
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
    [InlineData(BlockKind.HttpsPort)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.Dns)]
    public void Real_blocks_are_actionable(BlockKind kind)
    {
        Assert.True(Report(kind).Actionable);
    }

    /// <summary>У каждого исхода есть, что сказать человеку.</summary>
    [Theory]
    [InlineData(BlockKind.None)]
    [InlineData(BlockKind.TlsDpi)]
    [InlineData(BlockKind.HttpsPort)]
    [InlineData(BlockKind.Full)]
    [InlineData(BlockKind.Dns)]
    [InlineData(BlockKind.NoAddress)]
    public void Every_kind_is_named(BlockKind kind)
    {
        var report = Report(kind);

        Assert.False(string.IsNullOrWhiteSpace(report.Describe()));
        Assert.False(string.IsNullOrWhiteSpace(report.Remedy()));
    }

    private static TargetReport Report(BlockKind kind) => new()
    {
        Host = "example.org",
        Tcp = Ok(),
        Tls12 = Ok(),
        Tls13 = Ok(),
        Http = Ok(),
        Kind = kind,
    };
}
