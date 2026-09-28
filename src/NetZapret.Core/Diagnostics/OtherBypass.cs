using System.Diagnostics;
using Microsoft.Win32;

namespace NetZapret.Core.Diagnostics;

/// <summary>Чужой обход, найденный рядом с нашим.</summary>
/// <param name="Name">Как его называют люди.</param>
/// <param name="Where">Чем опознан: процесс с номером или служба — чтобы можно было проверить.</param>
/// <param name="Running">Работает сейчас; <c>false</c> — служба, которая поднимет его при старте Windows.</param>
public sealed record OtherBypass(string Name, string Where, bool Running);

/// <summary>
/// Ищет другой обход DPI, запущенный рядом с нашим.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по обсуждению #8 (28.09): у человека десинк не помогал ни в одной
/// сборке, и первым вопросом к нему было «не запущены ли GoodbyeDPI или
/// служба Zapret от Flowseal». Все они сидят на WinDivert, и два перехватчика
/// на одном трафике мешают друг другу: пакет, уже переделанный одним, второй
/// переделывает поверх, а драйверы разных версий не уживаются вовсе.
/// Программа может ответить на этот вопрос сама, не спрашивая человека.
/// </para>
/// <para>
/// Свой winws2 отличается по номеру процесса из состояния надзора, а не
/// по пути: движок бывает взят из чужой установки Zapret (у владельца —
/// C:\Zapret\Dev), и по пути он выглядел бы чужим.
/// </para>
/// <para>
/// Только чтение: процессы и ветка служб в реестре. Останавливать чужое
/// программа не берётся — это решение человека.
/// </para>
/// </remarks>
public static class OtherBypassScan
{
    /// <summary>Имя исполняемого файла без расширения → как его назвать.</summary>
    private static readonly (string Process, string Name)[] Known =
    [
        ("winws", "Zapret (winws)"),
        ("winws2", "Zapret 2 (winws2)"),
        ("goodbyedpi", "GoodbyeDPI"),
    ];

    /// <param name="ours">Номера процессов нашего надзора — их не считаем.</param>
    public static IReadOnlyList<OtherBypass> Find(IReadOnlySet<int> ours)
    {
        var found = new List<OtherBypass>();

        foreach (var (process, name) in Known)
        {
            Process[] running;

            try
            {
                running = Process.GetProcessesByName(process);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var p in running)
            {
                using (p)
                {
                    if (!ours.Contains(p.Id))
                        found.Add(new(name, $"процесс {process}.exe, №{p.Id}", Running: true));
                }
            }
        }

        foreach (var (service, image) in AutoServices())
        {
            if (Classify(image) is { } name)
                found.Add(new(name, $"служба «{service}»", Running: false));
        }

        return found;
    }

    /// <summary>Чей обход запускает служба — по её исполняемому файлу.</summary>
    internal static string? Classify(string imagePath)
    {
        // Путь в кавычках — до закрывающей (в нём бывают пробелы: Program Files);
        // без кавычек — до «.exe», а не до первого пробела, по той же причине.
        var text = imagePath.Trim();
        string exe;

        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            exe = close > 0 ? text[1..close] : text.Trim('"');
        }
        else
        {
            int end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = end > 0 ? text[..(end + 4)] : text.Split(' ')[0];
        }

        var file = Path.GetFileNameWithoutExtension(exe);

        foreach (var (process, name) in Known)
        {
            if (string.Equals(file, process, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    /// <summary>Службы с автозапуском: имя и командная строка.</summary>
    /// <remarks>
    /// Наш автозапуск — задача планировщика, а не служба, так что всё найденное
    /// здесь чужое. Остановленная сейчас служба всё равно в счёт: при следующем
    /// старте Windows она поднимется раньше нас.
    /// </remarks>
    private static IEnumerable<(string Service, string Image)> AutoServices()
    {
        RegistryKey? services = null;

        try
        {
            services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        }
        catch (Exception)
        {
        }

        if (services is null)
            yield break;

        using (services)
        {
            foreach (var name in services.GetSubKeyNames())
            {
                string? image = null;
                bool auto = false;

                try
                {
                    using var key = services.OpenSubKey(name);
                    image = key?.GetValue("ImagePath") as string;
                    auto = key?.GetValue("Start") is int start && start == 2;
                }
                catch (Exception)
                {
                    // Ветка закрыта правами — пропускаем, судим по остальным.
                }

                if (auto && !string.IsNullOrWhiteSpace(image))
                    yield return (name, image);
            }
        }
    }
}
