using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Жалобы движка берутся только за нынешний прогон.
/// </summary>
/// <remarks>
/// <para>
/// Прежде читался хвост журнала целиком, и записи прошлых запусков
/// выдавались за нынешние. Стоило прямой ошибки в отчёте 16.09 в 21:44:
/// журнал стоял с 21:30, движки перезапустили в 21:43, за прогон движок
/// не записал ни строки — а раздел показал четыре жалобы, оставшиеся
/// от позапрошлого запуска, и по ним едва не была построена причина,
/// которой нет.
/// </para>
/// <para>
/// По длине файла, а не по времени в строке: движок пишет секунды
/// от собственного старта, они начинаются заново при каждом запуске,
/// и прошлое от нынешнего по ним не отличить.
/// </para>
/// </remarks>
public sealed class EngineLogMarkTests : IDisposable
{
    private readonly string _log = Path.Combine(
        Path.GetTempPath(), $"netzapret-enginelog-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        if (File.Exists(_log))
            File.Delete(_log);
    }

    private const string Old =
        "ERROR[0299] connection: open connection to scdn.co:443 using outbound/direct[direct]: "
        + "lookup scdn.co: empty result";

    private const string Fresh =
        "ERROR[0007] connection: open connection to scdn.co:443 using outbound/urltest[auto]: EOF";

    [Fact]
    public void A_complaint_from_a_previous_run_is_not_shown()
    {
        File.WriteAllText(_log, Old + Environment.NewLine);

        var mark = EngineLog.Position(_log);

        Assert.Empty(EngineLog.Complaints(["scdn.co"], _log, mark));
    }

    [Fact]
    public void A_complaint_written_after_the_mark_is_shown()
    {
        File.WriteAllText(_log, Old + Environment.NewLine);

        var mark = EngineLog.Position(_log);

        File.AppendAllText(_log, Fresh + Environment.NewLine);

        var found = EngineLog.Complaints(["scdn.co"], _log, mark);

        Assert.Single(found);
        Assert.Equal("EOF", found[0].Error);
        Assert.Equal("auto", found[0].Outbound);
    }

    /// <summary>Без метки видно всё — прежнее поведение, и оно теперь явное.</summary>
    [Fact]
    public void Without_a_mark_the_whole_tail_is_read()
    {
        File.WriteAllText(_log, Old + Environment.NewLine);

        Assert.Single(EngineLog.Complaints(["scdn.co"], _log));
    }

    /// <summary>
    /// Журнал обернулся — метка указывает в никуда, читаем с начала.
    /// </summary>
    /// <remarks>
    /// Хуже точного отсчёта, но лучше пустоты: молчание здесь читалось бы
    /// как «движку не на что жаловаться», то есть как утверждение, которого
    /// никто не мерил.
    /// </remarks>
    [Fact]
    public void A_rotated_log_is_read_from_the_start()
    {
        File.WriteAllText(_log, new string('x', 4096) + Environment.NewLine);

        var mark = EngineLog.Position(_log);

        File.WriteAllText(_log, Fresh + Environment.NewLine);

        Assert.Single(EngineLog.Complaints(["scdn.co"], _log, mark));
    }

    [Fact]
    public void A_missing_log_has_no_position()
    {
        Assert.Equal(0, EngineLog.Position(_log));
    }
}
