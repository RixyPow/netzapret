using System.Text.Json;

namespace NetZapret.Core.Updates;

/// <summary>
/// Сколько раз скачан архив программы с GitHub — по всем выпускам.
/// </summary>
/// <remarks>
/// <para>
/// Владелец, 24.09, спросил про счётчик установок и админ-панель
/// с пользователями. Своего счётчика у программы нет и не будет: запрос
/// на свой сервер виден провайдеру по имени и метит компьютер как «здесь
/// стоит NetZapret», а список пользователей с адресами опасен для всех,
/// кто в нём окажется. GitHub же скачивания считает сам, и спросить его —
/// тот же запрос, что проверка обновлений делает при каждом запуске:
/// ничего нового о человеке он не выдаёт.
/// </para>
/// <para>
/// Скачивание — не установка: один человек может скачать несколько
/// версий, а обновление из программы берёт тот же архив и тоже
/// засчитывается. Поэтому число и называется «скачиваний».
/// </para>
/// </remarks>
public static class DownloadStats
{
    /// <summary>Всего скачиваний архивов; <c>null</c> — GitHub не ответил.</summary>
    public static async Task<long?> TotalAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // GitHub отклоняет запросы без User-Agent.
            http.DefaultRequestHeaders.Add("User-Agent", "NetZapret");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            long total = 0;

            // Постранично: выпусков уже за сорок, а страница — до ста.
            for (int page = 1; page <= 20; page++)
            {
                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{UpdateCheck.Repository}/releases?per_page=100&page={page}",
                    cancellationToken);

                var (sum, count) = Sum(json);
                total += sum;

                if (count < 100)
                    break;
            }

            return total;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Сумма скачиваний всех вложений страницы выпусков и число выпусков на ней.</summary>
    public static (long Sum, int Releases) Sum(string json)
    {
        using var document = JsonDocument.Parse(json);

        long sum = 0;
        int releases = 0;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            releases++;

            if (!release.TryGetProperty("assets", out var assets))
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("download_count", out var count) && count.TryGetInt64(out var n))
                    sum += n;
            }
        }

        return (sum, releases);
    }
}
