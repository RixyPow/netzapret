namespace NetZapret.Core;

/// <summary>
/// Лежит ли папка внутри облачной синхронизации: OneDrive, Dropbox, Google Диск, Яндекс Диск.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт друга владельца 06.10: программа стояла в
/// <c>%USERPROFILE%\OneDrive\Рабочий стол\NetZapret</c> — Windows переносит
/// «Рабочий стол» в OneDrive, когда включено его резервное копирование,
/// и человек этого не знает. Клиент синхронизации открывает свежие файлы
/// на чтение и выгружает редкие «по требованию»: файл, занятый им, не заменит
/// подмена при обновлении (robocopy, ERROR 32), а журналы и списки движков
/// переписываются каждую минуту и уходят в облако.
/// </para>
/// <para>
/// OneDrive узнаётся по его же переменным окружения (личный и рабочий),
/// остальные — по имени папки в пути: своих переменных у них нет.
/// </para>
/// </remarks>
public static class CloudFolder
{
    /// <summary>Переменные, в которых OneDrive хранит свои корни.</summary>
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    /// <summary>Папки клиентов без своих переменных — по имени в пути.</summary>
    private static readonly (string Folder, string Name)[] Folders =
    [
        ("Dropbox", "Dropbox"),
        ("Google Drive", "Google Диск"),
        ("My Drive", "Google Диск"),
        ("YandexDisk", "Яндекс Диск"),
        ("Yandex.Disk", "Яндекс Диск"),
    ];

    /// <summary>Чья синхронизация держит папку; <c>null</c> — ничья.</summary>
    /// <param name="variable">Чтение переменной окружения — подменяется в тестах.</param>
    public static string? Of(string path, Func<string, string?>? variable = null)
    {
        variable ??= Environment.GetEnvironmentVariable;

        var full = Normalise(path);

        foreach (var name in OneDriveVariables)
        {
            if (variable(name) is { Length: > 0 } root && Inside(full, Normalise(root)))
                return "OneDrive";
        }

        var segments = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // «OneDrive» и рабочее «OneDrive - Компания» — и без переменных,
        // если папку перенесли или программа запущена от другого пользователя.
        if (segments.Any(s => s.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase)))
        {
            return "OneDrive";
        }

        foreach (var (folder, name) in Folders)
        {
            if (segments.Any(s => s.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        return null;
    }

    private static string Normalise(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    private static bool Inside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
