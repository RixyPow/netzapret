using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace NetZapret.Gui.Views;

/// <summary>
/// Журналы движков.
/// </summary>
/// <remarks>
/// <para>
/// Не свалка, а источник. Именно журнал sing-box ответил на вопрос, который
/// мы разбирали неделю: имя он разобрал верно — значит рукопожатие дошло
/// целым и десинк его не портил, — а выход, через который шло, оказался
/// не тем, что назван в шапке отчёта.
/// </para>
/// <para>
/// Читается хвост: журнал вырастает до мегабайтов за сутки, а занимает нас
/// то, что было сейчас.
/// </para>
/// </remarks>
public partial class LogView : UserControl
{
    /// <summary>Сколько последних строк показывать.</summary>
    private const int Tail = 600;

    private bool _errorsOnly;

    public LogView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
    }

    private static string Path(int source) => source switch
    {
        0 => System.IO.Path.Combine("runtime", "sing-box.log"),
        1 => System.IO.Path.Combine("runtime", "winws2.log"),
        2 => System.IO.Path.Combine("runtime", "supervisor.log"),
        _ => System.IO.Path.Combine("logs", "gui.log"),
    };

    private void Reload()
    {
        var path = Path(Source.SelectedIndex);

        if (!File.Exists(path))
        {
            Text.Text = string.Empty;
            Status.Text = $"Журнала нет: {path}. Он появится, когда движок запустится.";

            return;
        }

        try
        {
            var lines = ReadTail(path, Tail).Select(Strip);

            if (_errorsOnly)
                lines = lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("WARN", StringComparison.OrdinalIgnoreCase));

            var shown = lines.ToList();

            Text.Text = string.Join('\n', shown);

            var size = new FileInfo(path).Length / 1024.0;
            Status.Text = $"{path} — {size:N0} КБ, показаны последние {shown.Count} строк"
                + (_errorsOnly ? " с ошибками." : ".");

            // Вниз, к свежему: журнал читают ради последнего, а не первого.
            Scroller.ScrollToBottom();
        }
        catch (Exception ex)
        {
            Status.Text = "Журнал не читается: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Последние строки файла, который движок держит открытым.
    /// </summary>
    /// <remarks>
    /// Общий доступ обязателен: без него чтение отказывало бы всегда — то есть
    /// ровно тогда, когда журнал и нужен, при работающем движке.
    /// </remarks>
    private static string[] ReadTail(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var ring = new string[count];
        int written = 0;

        while (reader.ReadLine() is { } line)
            ring[written++ % count] = line;

        if (written <= count)
            return ring.Take(written).ToArray();

        return Enumerable.Range(0, count).Select(i => ring[(written + i) % count]).ToArray();
    }

    /// <summary>
    /// Снимает управляющие последовательности цвета.
    /// </summary>
    /// <remarks>
    /// Движок красит вывод даже при записи в файл, и без очистки строка
    /// приходит с ESC[31m посреди слова.
    /// </remarks>
    private static string Strip(string line) =>
        Regex.Replace(line, @"\x1B\[[0-9;]*m", string.Empty);

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void OnSource(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            Reload();
    }

    private void OnErrors(object sender, RoutedEventArgs e)
    {
        _errorsOnly = !_errorsOnly;
        ErrorsButton.Content = _errorsOnly ? "Все строки" : "Только ошибки";

        Reload();
    }
}
