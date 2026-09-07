using System.Text;

namespace NetZapret.Cli;

/// <summary>
/// Пишет вывод разом на экран и в файл.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт проверки — это то, что человек несёт показывать: в issue, в чат,
/// нам. До сих пор его переносили выделением мышью из окна консоли, теряя
/// половину при прокрутке и получая на выходе картинку вместо текста.
/// Форма issue просит тот же вывод, и просить его скриншотами — значит
/// заранее согласиться на неполный.
/// </para>
/// <para>
/// Подмена <see cref="Console.Out"/>, а не отдельный путь вывода в проверке.
/// Второй способ печатать означал бы, что однажды строку допишут только
/// в один из них, и файл разойдётся с экраном — молча, потому что сверять
/// их никто не станет.
/// </para>
/// <para>
/// Цвета в файл не попадают, и это правильно: они задаются свойством консоли,
/// а не потоком. В файле остаётся ровно текст.
/// </para>
/// </remarks>
public sealed class TranscriptWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly StreamWriter _file;

    private TranscriptWriter(TextWriter console, StreamWriter file)
    {
        _console = console;
        _file = file;
    }

    public override Encoding Encoding => _console.Encoding;

    /// <summary>
    /// Начинает запись; возвращает <c>null</c>, если файл открыть не удалось.
    /// </summary>
    /// <remarks>
    /// Неудача не должна отменять саму проверку: она идёт минуты и полезна
    /// сама по себе. Скажем, что записать не вышло, и продолжим на экран.
    /// </remarks>
    public static TranscriptWriter? Start(string path, out string? problem)
    {
        problem = null;

        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // UTF-8 без метки порядка байтов: файл читают и Windows, и веб-форма
            // GitHub, и метка в начале превращается в видимый мусор в первой
            // строке ровно там, где её будут читать первой.
            var file = new StreamWriter(full, append: false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            var writer = new TranscriptWriter(Console.Out, file);
            Console.SetOut(writer);

            return writer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = ex.Message;
            return null;
        }
    }

    public override void Write(char value)
    {
        _console.Write(value);
        _file.Write(value);
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        _file.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        _file.WriteLine(value);
    }

    /// <summary>Возвращает консоли её собственный поток и закрывает файл.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Console.SetOut(_console);
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}
