namespace NetZapret.Subscriptions;

/// <summary>
/// Подписка целиком: серверы плюс метаданные из заголовков ответа.
/// </summary>
/// <remarks>
/// Заголовки — не украшение: <c>subscription-userinfo</c> и <c>profile-title</c>
/// это ровно те данные, из которых клиенты рисуют группу подписки с квотой
/// и датой истечения.
/// </remarks>
public sealed record SubscriptionInfo
{
    public required IReadOnlyList<ProxyServer> Servers { get; init; }

    /// <summary>Название из <c>profile-title</c>; может быть в base64.</summary>
    public string? Title { get; init; }

    public long UploadBytes { get; init; }

    public long DownloadBytes { get; init; }

    /// <summary>Квота трафика; 0 означает «без ограничения».</summary>
    public long TotalBytes { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Рекомендованный интервал обновления в часах.</summary>
    public double? UpdateIntervalHours { get; init; }

    /// <summary>Поддержка поставщика из <c>support-url</c>; только http(s).</summary>
    public string? SupportUrl { get; init; }

    /// <summary>Страница подписки из <c>profile-web-page-url</c>: продлить, сменить тариф; только http(s).</summary>
    public string? WebPageUrl { get; init; }

    /// <summary>Объявление поставщика из <c>announce</c>, не длиннее <see cref="SubscriptionParser.AnnounceLimit"/>.</summary>
    public string? Announce { get; init; }

    /// <summary>
    /// Запасной адрес панели из <c>fallback-url</c> — пароль, как и сама ссылка.
    /// </summary>
    /// <remarks>Не показывается и не пишется в журнал.</remarks>
    public string? FallbackUrl { get; init; }

    /// <summary>
    /// Новый адрес подписки из <c>new-url</c> — поставщик переносит её. Пароль.
    /// </summary>
    public string? NewUrl { get; init; }

    /// <summary>Строки подписки, которые не удалось разобрать, с причиной.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public long UsedBytes => UploadBytes + DownloadBytes;

    public long? RemainingBytes => TotalBytes > 0 ? Math.Max(0, TotalBytes - UsedBytes) : null;

    /// <summary>За сколько до конца срока предупреждать — как Happ: за три дня.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(3);

    /// <summary>
    /// Срок кончается или кончился: «осталось 2 дн», «истекла»; <c>null</c> — до срока далеко или его нет.
    /// </summary>
    /// <remarks>
    /// Срок «86 дн» строка подписки показывала и прежде, но тем же серым,
    /// что и всё остальное, а напоминания не было вовсе: подписка кончалась,
    /// и человек узнавал об этом по умершему туннелю.
    /// </remarks>
    public string? ExpiryWarningText(DateTimeOffset now)
    {
        if (ExpiresAt is not { } until || until - now > ExpiryWarning)
            return null;

        if (until <= now)
            return "срок подписки истёк";

        var left = until - now;

        return left.TotalHours < 24
            ? $"подписка кончается через {Math.Max(1, (int)Math.Ceiling(left.TotalHours))} ч"
            : $"подписка кончается через {(int)Math.Ceiling(left.TotalDays)} дн";
    }

    /// <summary>Сколько серверов с настоящим адресом sing-box не поддерживает по протоколу или транспорту.</summary>
    /// <remarks>
    /// Заглушки без адреса (<c>0.0.0.0:1</c>, разделители и сообщения панели) сюда не входят:
    /// писать про них «sing-box не поддерживает» неверно (обсуждение №69).
    /// </remarks>
    public int UnsupportedCount => Servers.Count(s => s.HasDialableAddress && !s.IsSupportedBySingBox);

    /// <summary>
    /// Текст сообщений-заглушек панели (серверы без адреса вроде <c>0.0.0.0:1</c>),
    /// когда годных серверов в подписке не осталось ни одного; <c>null</c> — если есть рабочие
    /// серверы или заглушек нет.
    /// </summary>
    /// <remarks>
    /// Обсуждение №69 (14.11): панель при лимите устройств по HWID отдаёт вместо серверов
    /// записи <c>0.0.0.0:1</c> с текстом «Вы достигли лимита устройств» в имени. Прежде
    /// парсер отбрасывал их молча, и человек видел пустую подписку без объяснения.
    /// </remarks>
    public string? StubNotice()
    {
        if (Servers.Count == 0 || Servers.Any(s => s.IsUsableOutbound))
            return null;

        var notes = Servers
            .Where(s => !s.HasDialableAddress && !string.IsNullOrWhiteSpace(s.Tag))
            .Select(s => s.Tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (notes.Count == 0)
            return null;

        var text = string.Join(" · ", notes);
        return text.Length <= 220 ? text : text[..219].TrimEnd() + "…";
    }

    public static SubscriptionInfo Empty { get; } = new() { Servers = Array.Empty<ProxyServer>() };
}
