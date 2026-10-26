using System.Text;

namespace NetZapret.Android.Host;

/// <summary>
/// Журнал хозяина — <c>runtime/android.log</c>: что делали запуск, сборка
/// конфига и проверки, рядом с журналом движка.
/// </summary>
/// <remarks>
/// Писать сюда можно только то, что можно показать (CLAUDE.md, «Секреты»):
/// имена подписок, не ссылки; ни пароля ByeDPI, ни ключей серверов.
/// Сообщения библиотек этому правилу уже следуют, свои — тоже.
/// </remarks>
internal static class AndroidJournal
{
    private const long Limit = 1024 * 1024;
    private static readonly object Sync = new();

    public static string PathOf(string files) => Path.Combine(files, "runtime", "android.log");

    public static void Write(string category, string message)
    {
        try
        {
            var path = PathOf(Directory.GetCurrentDirectory());

            lock (Sync)
            {
                // Одно поколение прошлого журнала, как *.1.log у Windows.
                var info = new FileInfo(path);

                if (info.Exists && info.Length > Limit)
                    File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{category}] {message}\n", new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
            // Журнал — не повод уронить запуск.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
