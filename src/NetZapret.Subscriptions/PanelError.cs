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
    public static string Describe(Exception error)
    {
        var http = error as HttpRequestException ?? error.GetBaseException() as HttpRequestException;

        if (http?.StatusCode is { } code)
            return Status(code);

        return error.GetBaseException() switch
        {
            SocketException => "панель недоступна — соединение не установилось",
            TimeoutException => "панель не ответила вовремя",
            var inner => inner.Message,
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
