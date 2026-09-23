namespace NetZapret.Core.Rules;

/// <summary>
/// Имя сайта из того, что человек вписал в поле: ссылки, имени со звёздочкой, с косой.
/// </summary>
/// <remarks>
/// Жило в окне, в добавлении своего домена. С 23.09 поле «Свой домен»
/// и поиск в «Маршрутах» — одно поле, и один и тот же текст надо и искать,
/// и узнавать как имя для добавления; разбор должен быть один.
/// </remarks>
public static class DomainInput
{
    /// <summary>
    /// Имя сайта в нижнем регистре; <c>null</c> — на имя не похоже.
    /// </summary>
    /// <remarks>
    /// Из адреса берётся только имя: люди вставляют ссылку целиком, и правило
    /// на «https://example.com/page» не совпало бы ни с чем. Имя без точки —
    /// это поиск («дискорд», «youtube»), а не сайт.
    /// </remarks>
    public static string? Normalize(string? text)
    {
        var raw = (text ?? string.Empty).Trim().Trim('/').ToLowerInvariant();

        int scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            raw = raw[(scheme + 3)..];

        raw = raw.Split('/')[0].Split('?')[0].TrimStart('*', '.');

        // Порт в ссылке — не часть имени.
        int colon = raw.LastIndexOf(':');
        if (colon > 0 && raw[(colon + 1)..].All(char.IsDigit))
            raw = raw[..colon];

        if (raw.Length == 0 || !raw.Contains('.') || raw.EndsWith('.') || raw.Any(char.IsWhiteSpace))
            return null;

        return raw;
    }
}
