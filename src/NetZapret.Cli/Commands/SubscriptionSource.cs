using NetZapret.Subscriptions;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Получает подписку по ссылке или из файла.
/// </summary>
internal static class SubscriptionSource
{
    /// <summary>
    /// Загружает подписку. <c>--sub</c> принимает и URL, и путь к файлу с телом
    /// подписки: файл удобен для отладки, когда не хочется дёргать сеть.
    /// </summary>
    public static async Task<SubscriptionInfo?> LoadAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var value = cmd.Value("sub");

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.Error.WriteLine("Укажите --sub <ссылка или путь к файлу>.");
            return null;
        }

        if (File.Exists(value))
        {
            var body = await File.ReadAllTextAsync(value, cancellationToken);
            var (servers, errors) = SubscriptionParser.ParseBody(body);

            Console.WriteLine($"Подписка из файла: {value}");
            return SubscriptionParser.BuildInfo(servers, errors, null, Path.GetFileName(value), null);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
        {
            Console.Error.WriteLine($"'{value}' не похоже ни на файл, ни на ссылку.");
            return null;
        }

        using var client = new SubscriptionClient();
        var info = await client.FetchAsync(url, cancellationToken);

        Console.WriteLine($"Подписка загружена: {SubscriptionClient.Unwrap(url).Host}");
        return info;
    }

    public static void PrintSummary(SubscriptionInfo info)
    {
        if (!string.IsNullOrEmpty(info.Title))
            Console.WriteLine($"Название:   {info.Title}");

        if (info.TotalBytes > 0)
        {
            Console.WriteLine(
                $"Трафик:     {FormatBytes(info.UsedBytes)} из {FormatBytes(info.TotalBytes)} " +
                $"(осталось {FormatBytes(info.RemainingBytes ?? 0)})");
        }
        else if (info.UsedBytes > 0)
        {
            Console.WriteLine($"Трафик:     {FormatBytes(info.UsedBytes)}, квота не ограничена");
        }

        if (info.ExpiresAt is { } expires)
        {
            var left = expires - DateTimeOffset.Now;
            Console.WriteLine($"Истекает:   {expires.LocalDateTime:dd.MM.yyyy} (через {(int)left.TotalDays} дн.)");
        }

        if (info.UpdateIntervalHours is { } hours)
            Console.WriteLine($"Обновление: раз в {hours:0.##} ч.");

        Console.WriteLine($"Серверов:   {info.Servers.Count}");

        if (info.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Не разобрано строк: {info.Errors.Count}");
            foreach (var error in info.Errors.Take(10))
                Console.WriteLine($"  {error}");
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
