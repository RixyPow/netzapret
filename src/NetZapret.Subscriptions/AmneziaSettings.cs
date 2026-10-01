using System.Globalization;
using System.Text.Json.Nodes;

namespace NetZapret.Subscriptions;

/// <summary>
/// Маскировка AmneziaWG — из ссылки или файла <c>.conf</c> в блок <c>amnezia</c> sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Типы проверены на нашем движке (1.14.1-extended-2.7.2, <c>sing-box check</c>,
/// 01.10): <c>h1</c>–<c>h4</c> он принимает и числом, и строкой-диапазоном
/// («100-200», так их пишет AmneziaWG 2.0), <c>i1</c>–<c>i5</c> — строками,
/// а <c>jc</c>, <c>jmin</c>, <c>jmax</c>, <c>s1</c>–<c>s4</c> — только числом:
/// <c>"jc": "4"</c> в кавычках роняет весь конфиг, а с ним и все остальные
/// серверы. Поэтому нечисловое значение здесь — ошибка разбора этого ключа,
/// а не строка в конфиге.
/// </para>
/// <para>
/// Ключи в ссылке и в файле те же, что у самой AmneziaWG (<c>Jc</c>,
/// <c>H1</c>, <c>I1</c>), регистр не важен.
/// </para>
/// </remarks>
public static class AmneziaSettings
{
    private static readonly string[] Numbers = ["jc", "jmin", "jmax", "s1", "s2", "s3", "s4"];
    private static readonly string[] Headers = ["h1", "h2", "h3", "h4"];
    private static readonly string[] Signatures = ["i1", "i2", "i3", "i4", "i5"];

    /// <param name="get">Значение по имени ключа без учёта регистра, <c>null</c> — нет такого.</param>
    /// <returns>Блок JSON строкой; <c>null</c>, если маскировки нет — обычный WireGuard.</returns>
    /// <exception cref="FormatException">Числовой ключ задан не числом.</exception>
    public static string? From(Func<string, string?> get)
    {
        var block = new JsonObject();

        foreach (var key in Numbers)
        {
            var text = get(key)?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"параметр AmneziaWG {key.ToUpperInvariant()} = «{text}» — не число");

            block[key] = number;
        }

        foreach (var key in Headers)
        {
            var text = get(key)?.Trim();

            if (string.IsNullOrEmpty(text))
                continue;

            // До 2^32: у AmneziaWG заголовки беззнаковые 32-битные, в int не влезают.
            block[key] = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : text;
        }

        foreach (var key in Signatures)
        {
            var text = get(key)?.Trim();

            if (!string.IsNullOrEmpty(text))
                block[key] = text;
        }

        return block.Count == 0 ? null : block.ToJsonString();
    }
}
