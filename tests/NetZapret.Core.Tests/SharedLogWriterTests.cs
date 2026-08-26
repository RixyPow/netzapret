using System.Text;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Журнал, в который пишут несколько супервизоров сразу.
/// </summary>
/// <remarks>
/// Прежняя запись — обычный поток поверх файла, открытого на дописывание, —
/// портила его: позиция запоминалась при открытии, и второй процесс писал
/// поверх написанного первым. На разборе утреннего сбоя из-за этого пропало
/// сообщение о причине, ради которого журнал и заводился.
/// </remarks>
public sealed class SharedLogWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"netzapret-log-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void ParallelWritersDoNotOverwriteEachOther()
    {
        const int writers = 8;
        const int linesEach = 60;

        // Каждый со своим экземпляром, как разные процессы: общий у них
        // только файл и мьютекс по его пути.
        Parallel.For(0, writers, i =>
        {
            var writer = SharedLogWriter.TryOpen(_path);
            Assert.NotNull(writer);

            for (int n = 0; n < linesEach; n++)
                writer!.WriteLine($"писатель-{i} строка-{n}");
        });

        var lines = File.ReadAllLines(_path, Encoding.UTF8)
            .Where(l => l.Length > 0)
            .ToList();

        Assert.Equal(writers * linesEach, lines.Count);

        // Ни одной обрезанной или склеенной строки: порча выглядела именно так —
        // «не запустился — про<мусор>2026-08-26 sing-box: не запус».
        Assert.All(lines, line =>
            Assert.Matches(@"^писатель-\d+ строка-\d+$", line));

        // И каждая написанная строка на месте, а не только их количество.
        for (int i = 0; i < writers; i++)
            Assert.Equal(linesEach, lines.Count(l => l.StartsWith($"писатель-{i} ", StringComparison.Ordinal)));
    }

    [Fact]
    public void ExistingContentIsKept()
    {
        // Дописывание, а не перезапись: между перезапусками важно видеть,
        // чем закончился прошлый.
        File.WriteAllText(_path, "прошлый запуск\n", new UTF8Encoding(false));

        var writer = SharedLogWriter.TryOpen(_path);
        writer!.WriteLine("нынешний запуск");

        var text = File.ReadAllText(_path, Encoding.UTF8);

        Assert.Contains("прошлый запуск", text);
        Assert.Contains("нынешний запуск", text);
    }

    [Fact]
    public void CyrillicSurvivesTheRoundTrip()
    {
        // Журнал читается меню и человеком; кодировка здесь не мелочь.
        var writer = SharedLogWriter.TryOpen(_path);
        writer!.WriteLine("sing-box: не запустился — проверка живости не прошла");

        Assert.Contains(
            "не запустился — проверка живости не прошла",
            File.ReadAllText(_path, Encoding.UTF8));
    }

    [Fact]
    public void AnOversizedLogIsTrimmedToItsTail()
    {
        // Хвост полезнее головы, и без усечения файл за недели станет
        // неподъёмным.
        var filler = string.Join("\n", Enumerable.Range(0, 40_000).Select(i => $"строка {i} " + new string('x', 40)));
        File.WriteAllText(_path, filler, new UTF8Encoding(false));

        Assert.True(new FileInfo(_path).Length > 512 * 1024);

        SharedLogWriter.TryOpen(_path);

        var lines = File.ReadAllLines(_path, Encoding.UTF8);

        Assert.InRange(lines.Length, 1, 501);
        Assert.Contains("39999", lines[^1]);
    }

    [Fact]
    public void AnImpossiblePathIsNotAnError()
    {
        // Отказ вести журнал не должен мешать запуску движков.
        Assert.Null(SharedLogWriter.TryOpen("Z:\\нет-такого-диска\\журнал.log"));
    }
}
