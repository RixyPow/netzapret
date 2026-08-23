using System.Text;

namespace NetZapret.Supervisor;

/// <summary>
/// Файл лога с ограничением размера.
/// </summary>
/// <remarks>
/// <para>
/// Обязателен для автозапуска: winws2 пишет строку на каждое соединение,
/// и за сутки непрерывной работы файл вырос бы до сотен мегабайт. Ограничение
/// по размеру с парой поколений решает это, не требуя внешнего планировщика.
/// </para>
/// <para>
/// Писатель держится открытым, а не открывается на каждую строку: движки
/// выдают вывод пачками, и открытие файла на строку заметно нагружало бы диск.
/// </para>
/// </remarks>
public sealed class RollingLog : IDisposable
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _generations;
    private readonly object _sync = new();

    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    /// <param name="maxBytes">Порог, после которого файл сменяется.</param>
    /// <param name="generations">Сколько прошлых файлов хранить.</param>
    public RollingLog(string path, long maxBytes = 4 * 1024 * 1024, int generations = 2)
    {
        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _generations = Math.Max(0, generations);
    }

    public void AppendLine(string line)
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            try
            {
                EnsureWriter();

                if (_written >= _maxBytes)
                {
                    Rotate();
                    EnsureWriter();
                }

                _writer!.WriteLine(line);

                // Учитываем приблизительно: точный размер не нужен, важен порядок.
                _written += line.Length + Environment.NewLine.Length;
            }
            catch (IOException)
            {
                // Лог не должен ронять службу, за которой следит супервизор.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
            return;

        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var info = new FileInfo(_path);
        _written = info.Exists ? info.Length : 0;

        _writer = new StreamWriter(_path, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    /// <summary>
    /// Сдвигает поколения: текущий файл становится .1, .1 становится .2 и так далее.
    /// </summary>
    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            // Самое старое поколение удаляется, остальные сдвигаются вниз.
            var oldest = Generation(_generations);

            if (File.Exists(oldest))
                File.Delete(oldest);

            for (int i = _generations - 1; i >= 1; i--)
            {
                var from = Generation(i);

                if (File.Exists(from))
                    File.Move(from, Generation(i + 1), overwrite: true);
            }

            if (_generations >= 1 && File.Exists(_path))
                File.Move(_path, Generation(1), overwrite: true);
            else if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
            // Не вышло сдвинуть — продолжаем писать в текущий файл.
        }

        _written = 0;
    }

    private string Generation(int index) => $"{_path}.{index}";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
