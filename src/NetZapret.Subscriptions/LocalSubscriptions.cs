using System.Text;

namespace NetZapret.Subscriptions;

/// <summary>
/// Подписки, загруженные локальным файлом (mihomo YAML, Xray JSON, base64).
/// </summary>
/// <remarks>
/// <para>
/// Хранятся копией в <c>config\subscriptions\&lt;id&gt;.txt</c>, а в книге подписок
/// ссылаются адресом <c>local://&lt;id&gt;</c>. Копия нужна по двум причинам:
/// человек часто открывает файл прямо из «Загрузок» Telegram и позже его удаляет,
/// а перенос папки NetZapret на другой компьютер потерял бы абсолютный путь.
/// </para>
/// <para>
/// Файлы содержат ключи серверов и равносильны паролю: каталог в <c>.gitignore</c>,
/// в архив выпуска (<c>pack.cmd</c>) и отчёт поддержки (<c>SupportReport</c>)
/// не попадает, а при удалении подписки лишние файлы стираются (<see cref="KeepOnly"/>).
/// </para>
/// </remarks>
public static class LocalSubscriptions
{
    public const string Scheme = "local://";

    /// <summary>Предел размера загружаемого файла подписки или ключа (5 МБ).</summary>
    public const long MaxFileBytes = 5_000_000;

    /// <summary>Где лежат копии локальных подписок рядом с <c>subscriptions.json</c>.</summary>
    public static string DefaultDirectory => Path.Combine("config", "subscriptions");

    /// <summary>Указывает ли адрес на локальную подписку.</summary>
    public static bool IsLocal(string? url) => TryId(url) is not null;

    /// <summary>
    /// Извлекает безопасный шестнадцатеричный идентификатор из <c>local://&lt;id&gt;</c>;
    /// <c>null</c> — это не локальная ссылка или в ней запрещённые знаки.
    /// </summary>
    public static string? TryId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();

        if (!trimmed.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var id = trimmed[Scheme.Length..];

        if (id.Length is < 8 or > 64 || !id.All(Uri.IsHexDigit))
            return null;

        return id.ToLowerInvariant();
    }

    /// <summary>Путь к файлу тела локальной подписки; <c>null</c> — ссылка не локальная.</summary>
    public static string? FilePath(string? url, string? directory = null) =>
        TryId(url) is { } id
            ? Path.Combine(directory ?? DefaultDirectory, id + ".txt")
            : null;

    /// <summary>Сохраняет тело новой локальной подписки и возвращает её адрес <c>local://…</c>.</summary>
    public static string Save(string body, string? directory = null)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        var url = Scheme + id;
        WriteAtomically(FilePath(url, directory)!, body);
        return url;
    }

    /// <summary>Заменяет тело существующей локальной подписки на месте.</summary>
    public static bool Replace(string url, string body, string? directory = null)
    {
        if (FilePath(url, directory) is not { } path)
            return false;

        WriteAtomically(path, body);
        return true;
    }

    /// <summary>Читает и разбирает локальную подписку с диска; <c>null</c> — файла нет.</summary>
    public static (SubscriptionInfo Info, DateTimeOffset At)? Read(string url, string? directory = null)
    {
        if (FilePath(url, directory) is not { } path || !File.Exists(path))
            return null;

        var body = File.ReadAllText(path);
        var (servers, errors) = SubscriptionParser.ParseBody(body);

        return (new SubscriptionInfo { Servers = servers, Errors = errors }, File.GetLastWriteTime(path));
    }

    /// <summary>Стирает файлы локальных подписок, которых уже нет в книге; возвращает, сколько стёрто.</summary>
    public static int KeepOnly(IEnumerable<string?> urls, string? directory = null)
    {
        var folder = directory ?? DefaultDirectory;

        if (!Directory.Exists(folder))
            return 0;

        var keep = urls
            .Select(TryId)
            .Where(id => id is not null)
            .Select(id => id + ".txt")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int removed = 0;

        foreach (var file in Directory.GetFiles(folder, "*.txt"))
        {
            if (keep.Contains(Path.GetFileName(file)))
                continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception)
            {
                // Занят или нет прав — сотрётся при следующей чистке.
            }
        }

        return removed;
    }

    private static void WriteAtomically(string path, string body)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temp, body, new UTF8Encoding(false));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }
}
