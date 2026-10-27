using ZXing;
using ZXing.Common;

namespace NetZapret.Subscriptions;

/// <summary>Что нашлось во вставленном: ключи, ссылка подписки или ничего.</summary>
/// <param name="Keys">Ключи — уже в виде строк для «Отдельных ключей».</param>
/// <param name="SubscriptionUrl">Ссылка подписки, обёртки клиентов уже развёрнуты.</param>
/// <param name="Problem">Почему не нашлось ничего — словами для строки состояния, без самой ссылки.</param>
/// <param name="Note">Оговорка к найденным ключам — например, маскировка AmneziaWG, которой движок не знает.</param>
public sealed record ImportFound(IReadOnlyList<string> Keys, string? SubscriptionUrl, string? Problem, string? Note = null);

/// <summary>
/// Ключи и подписки из буфера, файла и QR-кода.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 01.10: «ключ из буфера или по QR можно». Ключи раздают
/// по-разному: строкой в боте, QR-кодом на сайте продавца, файлом .conf
/// у WireGuard и AmneziaVPN. Добавлять всё это прежде можно было только
/// вставкой в поле, а QR и файл — никак.
/// </para>
/// <para>
/// Файл .conf превращается в ссылку <c>wireguard://</c>
/// (<see cref="WireGuardConf.ToLink"/>) и дальше живёт обычным ключом:
/// хранится, показывается и убирается так же. Ключ AmneziaVPN <c>vpn://</c>
/// (<see cref="AmneziaKey"/>) — тоже.
/// </para>
/// </remarks>
public static class KeyImport
{
    /// <param name="text">Вставленный текст или содержимое файла.</param>
    /// <param name="name">Имя для сервера из файла .conf — обычно имя файла.</param>
    public static ImportFound FromText(string? text, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new([], null, "пусто");

        if (WireGuardConf.Looks(text))
        {
            return WireGuardConf.TryParse(text, name, out var server, out var error)
                ? Found([WireGuardConf.ToLink(server!)])
                : new([], null, "файл WireGuard не разбирается: " + error);
        }

        // Ключ AmneziaVPN живёт дальше так же, как файл .conf, — ссылкой
        // wireguard:// (обсуждение №40). Обычные ключи рядом с ним не теряются.
        if (AmneziaKey.Looks(text))
        {
            var links = new List<string>();
            string? problem = null;

            foreach (var key in AmneziaKey.Find(text))
            {
                if (AmneziaKey.TryParse(key, name, out var servers, out var error))
                    links.AddRange(servers.Select(WireGuardConf.ToLink));
                else
                    problem ??= error;
            }

            links.AddRange(KeyRing.Split(text));

            return links.Count > 0
                ? Found(links)
                : new([], null, "ключ AmneziaVPN не разбирается: " + (problem ?? "ключа vpn:// не нашлось"));
        }

        var keys = KeyRing.Split(text);

        if (keys.Count > 0)
            return Found(keys);

        var line = text.Trim();

        if (!line.Contains(' ') && Uri.TryCreate(line, UriKind.Absolute, out var uri))
        {
            var unwrapped = SubscriptionClient.Unwrap(uri);

            if (unwrapped.Scheme == Uri.UriSchemeHttp || unwrapped.Scheme == Uri.UriSchemeHttps)
                return new([], unwrapped.ToString(), null);
        }

        return new([], null, "ни ключей, ни ссылки подписки");
    }

    /// <summary>
    /// Ключи — с оговоркой, если у AmneziaWG есть маскировка, которой движок не знает.
    /// </summary>
    /// <remarks>
    /// Такой сервер добавляется: параметры едут в ссылке и заработают, когда их
    /// узнает движок. Но молчать о них нельзя — без них сервер AmneziaWG
    /// рукопожатие не примет, и «не отвечает» выглядело бы загадкой (№40, 28.10).
    /// </remarks>
    private static ImportFound Found(IReadOnlyList<string> keys)
    {
        var unknown = keys
            .SelectMany(k => ProxyUriParser.TryParse(k, out var server, out _) && server!.IsEndpoint
                ? AmneziaSettings.Unsupported(server.Extra.Keys)
                : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(keys, null, null, unknown.Count == 0 ? null
            : $"параметров AmneziaWG {string.Join(", ", unknown)} движок не знает — сервер может не ответить");
    }

    /// <summary>
    /// Текст QR-кода из картинки; <c>null</c> — кода нет или он не читается.
    /// </summary>
    /// <param name="bgra">Пиксели по четыре байта, синий-зелёный-красный-прозрачность.</param>
    /// <remarks>
    /// Пикселями, а не картинкой WPF: разбор — в библиотеке, где его зовут
    /// и окно, и тесты, а WPF здесь знать незачем. Снимок экрана с кодом
    /// в углу — частый случай, поэтому с «постараться сильнее».
    /// </remarks>
    public static string? ReadQr(byte[] bgra, int width, int height)
    {
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.QR_CODE],
            },
        };

        return reader.Decode(source)?.Text;
    }
}
