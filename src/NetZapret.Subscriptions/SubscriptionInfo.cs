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

    /// <summary>Строки подписки, которые не удалось разобрать, с причиной.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public long UsedBytes => UploadBytes + DownloadBytes;

    public long? RemainingBytes => TotalBytes > 0 ? Math.Max(0, TotalBytes - UsedBytes) : null;

    public static SubscriptionInfo Empty { get; } = new() { Servers = Array.Empty<ProxyServer>() };
}
