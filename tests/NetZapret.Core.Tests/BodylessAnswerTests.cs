using System.Net;
using System.Net.Sockets;
using System.Text;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ответ, которому тело не положено, засчитывается как полученный.
/// </summary>
/// <remarks>
/// <para>
/// Проба данных выходила из чтения только дочитав обещанное Content-Length.
/// У перенаправления его обычно нет вовсе — содержимое такого ответа это
/// заголовок Location, — и проба читала «до упора». Упором оказывалось
/// закрытие сессии.
/// </para>
/// <para>
/// Стоило строки в отчёте: whatsapp.net отвечает «302 Found» в 403 байтах,
/// замер 2026-09-16 — рукопожатие проходит всеми тремя способами, GET /
/// отдаёт перенаправление. А проба звала это «Получено непредвиденное
/// сообщение или оно имеет неправильный формат» и объявляла работающий
/// мессенджер сломанным.
/// </para>
/// <para>
/// Проверяется через разбор заголовков, а не живой сетью: поднимать TLS
/// ради трёх строк текста незачем, а ломается именно разбор — в том,
/// где считается конец ответа.
/// </para>
/// </remarks>
public sealed class BodylessAnswerTests
{
    [Theory]
    [InlineData("HTTP/1.1 302 Found\r\nLocation: https://www.whatsapp.com/\r\n\r\n", 302)]
    [InlineData("HTTP/1.1 301 Moved Permanently\r\nLocation: https://example.com/\r\n\r\n", 301)]
    [InlineData("HTTP/1.1 204 No Content\r\n\r\n", 204)]
    [InlineData("HTTP/1.1 304 Not Modified\r\n\r\n", 304)]
    public void A_bodyless_answer_is_recognised(string response, int expected)
    {
        var bytes = Encoding.ASCII.GetBytes(response);

        Assert.Equal(expected, BlockCheck.ParseStatus(bytes, bytes.Length));

        // Конец заголовков найден — значит ответ считается полученным
        // целиком, и дочитывать нечего.
        Assert.Equal(bytes.Length, BlockCheck.HeaderLength(bytes, bytes.Length));
    }

    /// <summary>
    /// Обычный ответ с телом по-прежнему требует тела.
    /// </summary>
    /// <remarks>
    /// Смысл был в том, чтобы засчитать ответ, у которого тела нет, а не
    /// в том, чтобы перестать замечать оборванные. Именно на оборванном теле
    /// ловится steamcommunity.com: рукопожатие проходит, а поток умирает
    /// на четырнадцатой тысяче байт.
    /// </remarks>
    [Fact]
    public void An_answer_with_a_body_still_needs_one()
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5000\r\n\r\nhello");

        Assert.Equal(200, BlockCheck.ParseStatus(bytes, bytes.Length));
        Assert.Equal(5000, BlockCheck.ParseContentLength(bytes, bytes.Length));
    }

    /// <summary>
    /// Живой замер: перенаправление доходит и не зовётся обрывом.
    /// </summary>
    /// <remarks>
    /// Поддельный сервер отдаёт 302 и молчит, не закрывая соединение, —
    /// ровно то поведение, на котором проба спотыкалась. Раньше она ждала
    /// тишину три секунды и объявляла поток замершим.
    /// </remarks>
    [Fact]
    public async Task A_redirect_that_is_followed_by_silence_still_counts()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = listener.AcceptTcpClientAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        using var server = await accepting;

        var answer = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\nLocation: https://www.whatsapp.com/\r\n\r\n");

        await server.GetStream().WriteAsync(answer);

        var buffer = new byte[1024];
        int read = await client.GetStream().ReadAsync(buffer);

        Assert.Equal(302, BlockCheck.ParseStatus(buffer, read));
        Assert.True(BlockCheck.HeaderLength(buffer, read) > 0);

        listener.Stop();
    }
}
