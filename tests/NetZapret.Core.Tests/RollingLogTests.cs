using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ротация логов: без неё winws2 под автозапуском пишет строку на каждое
/// соединение и за сутки раздувает файл до сотен мегабайт.
/// </summary>
public sealed class RollingLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netzapret-log-{Guid.NewGuid():N}");
    private readonly string _path;

    public RollingLogTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "service.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void LinesAreWritten()
    {
        using (var log = new RollingLog(_path))
        {
            log.AppendLine("первая");
            log.AppendLine("вторая");
        }

        var lines = File.ReadAllLines(_path);

        Assert.Equal(2, lines.Length);
        Assert.Equal("первая", lines[0]);
    }

    [Fact]
    public void FileIsRotatedOnceTheLimitIsPassed()
    {
        using (var log = new RollingLog(_path, maxBytes: 100, generations: 2))
        {
            for (int i = 0; i < 40; i++)
                log.AppendLine($"строка номер {i} с некоторым содержимым");
        }

        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(_path + ".1"), "прошлое поколение должно сохраниться");
    }

    [Fact]
    public void OldGenerationsAreDiscarded()
    {
        // Иначе ограничение размера не ограничивает ничего:
        // место занимают накопившиеся поколения.
        using (var log = new RollingLog(_path, maxBytes: 50, generations: 2))
        {
            for (int i = 0; i < 100; i++)
                log.AppendLine($"строка {i} подлиннее, чтобы порог проходился часто");
        }

        Assert.False(File.Exists(_path + ".3"), "третьего поколения быть не должно");

        long total = Directory.EnumerateFiles(_directory).Sum(f => new FileInfo(f).Length);
        Assert.True(total < 5000, $"суммарный размер вышел за разумные рамки: {total}");
    }

    [Fact]
    public void ExistingFileIsAppendedNotTruncated()
    {
        // Лог прошлого запуска нужен для разбора падения.
        File.WriteAllText(_path, "старая строка" + Environment.NewLine);

        using (var log = new RollingLog(_path))
            log.AppendLine("новая строка");

        Assert.Contains("старая строка", File.ReadAllText(_path));
    }

    [Fact]
    public void MissingDirectoryIsCreated()
    {
        var nested = Path.Combine(_directory, "вложенный", "service.log");

        using (var log = new RollingLog(nested))
            log.AppendLine("строка");

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void WritingAfterDisposeIsIgnoredNotThrown()
    {
        // Насосы вывода живут своей жизнью и могут дописать строку
        // уже после остановки супервизора.
        var log = new RollingLog(_path);
        log.AppendLine("до");
        log.Dispose();

        var exception = Record.Exception(() => log.AppendLine("после"));

        Assert.Null(exception);
    }

    [Fact]
    public void LogCanBeReadWhileTheServiceIsWriting()
    {
        // Разбирают лог именно во время работы службы. Первая версия
        // держала файл монопольно, и прочитать его было нельзя.
        using var log = new RollingLog(_path);
        log.AppendLine("строка при живом писателе");

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        Assert.Contains("строка при живом писателе", reader.ReadToEnd());
    }

    [Fact]
    public void ConcurrentWritersDoNotLoseOrCorruptLines()
    {
        // stdout и stderr пишутся двумя независимыми задачами.
        const int perWriter = 200;

        using (var log = new RollingLog(_path, maxBytes: 1024 * 1024))
        {
            Parallel.For(0, 4, writer =>
            {
                for (int i = 0; i < perWriter; i++)
                    log.AppendLine($"писатель {writer} строка {i}");
            });
        }

        Assert.Equal(4 * perWriter, File.ReadAllLines(_path).Length);
    }
}
