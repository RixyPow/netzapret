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
/// Проверяется разбором заголовков, и только им. Здесь стоял ещё один
/// тест, звавшийся живым замером: он поднимал слушателя на петле, писал
/// в сокет те же байты, вычитывал их обратно и звал тот же разборщик.
/// Сокеты в нём не проверяли ничего — ни строки кода пробы через них
/// не проходило, — а название обещало обратное. Удалён 17.09.
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
}
