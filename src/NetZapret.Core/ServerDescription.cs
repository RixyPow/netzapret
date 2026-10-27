using System.Text;

namespace NetZapret.Core;

/// <summary>
/// Описание сервера, приклеенное панелью к его имени: <c>имя?serverDescription=base64</c>.
/// </summary>
/// <remarks>
/// <para>
/// Так делает Remnawave для «своих» клиентов — Happ, FlClash X, INCY
/// (<c>xray.generator.service.ts</c>: <c>link?serverDescription=…</c>, с
/// <c>extended-clients.ts</c>). Мы спрашиваем панель под именем Happ, когда
/// под sing-box она отказала (<c>SubscriptionClient</c>), и получаем то же.
/// Happ хвост прячет и показывает описание строкой под именем.
/// </para>
/// <para>
/// Отчёт DxHell 27.10: у каждого сервера подписки в имени стояло
/// «🇷🇺 Обход 💫?serverDescription=0JTQu9GP…» — «Для LTE (белые списки)»
/// и «Для WiFi (не тратит ГБ)», то есть ровно то, что человеку и нужно
/// было прочитать, чтобы выбрать сервер для своей сети.
/// </para>
/// </remarks>
public static class ServerDescription
{
    private const string Marker = "?serverDescription=";

    /// <summary>Имя без хвоста и описание словами; хвоста нет — имя как есть и <c>null</c>.</summary>
    public static (string Name, string? Description) Split(string remark)
    {
        int at = remark.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return (remark, null);

        var name = remark[..at].Trim();
        var raw = remark[(at + Marker.Length)..].Trim();

        // Имя из одного хвоста — не имя: лучше оставить как пришло.
        if (name.Length == 0)
            return (remark, null);

        return (name, Decode(raw));
    }

    /// <summary>Имя без хвоста описания.</summary>
    /// <remarks>
    /// Теги серверов в настройках записаны прежними именами — с хвостом.
    /// Снимать его при чтении, иначе закреплённый сервер и исключения
    /// автоподбора после обновления не нашлись бы.
    /// </remarks>
    public static string Strip(string remark) => Split(remark).Name;

    private static string? Decode(string raw)
    {
        if (raw.Length == 0)
            return null;

        var text = raw.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();

            // Не текст — значит, не base64, а описание словами.
            return decoded.Length > 0 && !decoded.Any(c => char.IsControl(c)) ? decoded : raw;
        }
        catch (FormatException)
        {
            return raw;
        }
    }
}
