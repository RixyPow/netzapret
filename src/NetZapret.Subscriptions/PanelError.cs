using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NetZapret.Subscriptions;

/// <summary>
/// Почему панель подписки не отдала список — словами, а не текстом исключения.
/// </summary>
/// <remarks>
/// Владелец 01.10: «панель подвела? что за прикол?» — под подпиской стояло
/// «панель подвела (Response status code does not indicate success: 502
/// (Bad Gateway).)». Панель Trust в тот час отвечала 502 за 0,1 с: её сервер
/// жив, но отдаёт ошибку — неполадка у продавца, не у нас. Код ответа
/// говорит, чья это беда и что делать, — его и называем.
/// </remarks>
public static class PanelError
{
    public static string Describe(Exception error) => Describe(error, NetZapret.Core.PhysicalNetwork.Up);

    /// <param name="error">Отказ чтения.</param>
    /// <param name="online">
    /// Есть ли сеть. Спрашивается только при отказе соединения: ответ панели
    /// с кодом сеть доказывает и сам.
    /// </param>
    /// <remarks>
    /// Без сети панель ни при чём (23–24.10, <see cref="NetZapret.Core.PhysicalNetwork"/>):
    /// Windows без адаптера сразу отвечает «такого имени нет», и до 24.10 это
    /// называлось «панель недоступна».
    /// </remarks>
    public static string Describe(Exception error, Func<bool> online)
    {
        var http = error as HttpRequestException ?? error.GetBaseException() as HttpRequestException;

        if (http?.StatusCode is { } code)
            return Status(code);

        var inner = error.GetBaseException();

        if (inner is SocketException or TimeoutException && !online())
            return NetZapret.Core.PhysicalNetwork.Offline;

        return inner switch
        {
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }
                => "имя панели не разрешилось — DNS не знает его или не ответил",
            SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                => "панель отказала в соединении — сервер жив, но порт закрыт",
            SocketException => "панель недоступна — соединение не установилось",
            TimeoutException => "панель не ответила вовремя",
            _ => inner.Message,
        };
    }

    /// <summary>Код ответа панели словами.</summary>
    public static string Status(HttpStatusCode code)
    {
        int number = (int)code;

        return number switch
        {
            401 or 403 => $"панель отказала ({number}) — ссылка устарела или подписка закрыта",
            404 => "панель не знает такой ссылки (404) — подписку могли сменить",
            429 => "панель просит обращаться реже (429)",
            >= 500 and < 600 => $"панель ответила ошибкой {number} — неполадка на сервере продавца",
            _ => $"панель ответила кодом {number}",
        };
    }
}
