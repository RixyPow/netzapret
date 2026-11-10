using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Просьба о движках файлом: исполняется один раз, только свежая, только с известным глаголом,
/// а просивший узнаёт, забрали ли её и что ответили.
/// </summary>
public sealed class EngineRequestsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nz-engine-requests-" + Guid.NewGuid().ToString("N"));

    private string Request => Path.Combine(_folder, "engines.request.json");

    private string Reply => Path.Combine(_folder, "engines.reply.json");

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 0, 30, 0, TimeSpan.FromHours(3));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_request_is_taken_once()
    {
        var asked = EngineRequests.Ask(EngineVerb.Restart, "nz", Now, Request);

        Assert.True(EngineRequests.Pending(asked.Id, Request));

        var taken = EngineRequests.Take(Now.AddSeconds(1), Request);

        Assert.NotNull(taken);
        Assert.Equal(asked.Id, taken.Id);
        Assert.Equal(EngineVerb.Restart, taken.Verb);
        Assert.Equal("nz", taken.From);

        // Забрали — файла нет: второй круг окна не перезапустит движки ещё раз.
        Assert.False(File.Exists(Request));
        Assert.False(EngineRequests.Pending(asked.Id, Request));
        Assert.Null(EngineRequests.Take(Now.AddSeconds(2), Request));
    }

    [Fact]
    public void A_stale_request_is_dropped_not_done()
    {
        EngineRequests.Ask(EngineVerb.Stop, "nz", Now, Request);

        // Окно открыли через минуту после просьбы: гасить движки уже не просят.
        Assert.Null(EngineRequests.Take(Now + EngineRequests.Fresh + TimeSpan.FromSeconds(1), Request));
        Assert.False(File.Exists(Request));
    }

    [Fact]
    public void A_request_from_the_future_is_dropped_too()
    {
        EngineRequests.Ask(EngineVerb.Stop, "nz", Now.AddMinutes(5), Request);

        Assert.Null(EngineRequests.Take(Now, Request));
        Assert.False(File.Exists(Request));
    }

    [Theory]
    [InlineData("не json вовсе")]
    [InlineData("{}")]
    [InlineData("""{"Id":"1","Verb":"format-disk","From":"nz","At":"2026-11-11T00:30:00+03:00"}""")]
    [InlineData("""{"Id":"1","Verb":7,"From":"nz","At":"2026-11-11T00:30:00+03:00"}""")]
    [InlineData("""{"Id":" ","Verb":"Stop","From":"nz","At":"2026-11-11T00:30:00+03:00"}""")]
    public void Garbage_is_removed_and_does_nothing(string text)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Request, text);

        Assert.Null(EngineRequests.Take(Now, Request));
        Assert.False(File.Exists(Request));
    }

    [Fact]
    public void The_asker_name_goes_to_the_journal_as_one_short_line()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Request,
            """{"Id":"1","Verb":"Start","From":"nz\r\n[12:00:00] движки: остановка — человек, и ещё сорок знаков хвоста","At":"2026-11-11T00:30:00+03:00"}""");

        var taken = EngineRequests.Take(Now, Request);

        Assert.NotNull(taken);
        Assert.DoesNotContain('\n', taken.From);
        Assert.DoesNotContain('\r', taken.From);
        Assert.True(taken.From.Length <= 24);
        Assert.StartsWith("nz", taken.From);
    }

    [Fact]
    public void Withdraw_removes_only_ones_own_request()
    {
        var first = EngineRequests.Ask(EngineVerb.Start, "nz", Now, Request);
        var second = EngineRequests.Ask(EngineVerb.Stop, "nz", Now, Request);

        // Первую заменила вторая: отзыв первой чужую просьбу не трогает.
        EngineRequests.Withdraw(first.Id, Request);
        Assert.True(EngineRequests.Pending(second.Id, Request));

        EngineRequests.Withdraw(second.Id, Request);
        Assert.False(File.Exists(Request));
    }

    [Fact]
    public void A_reply_is_read_only_by_the_one_who_asked()
    {
        Assert.Null(EngineRequests.Answered("a", Reply));

        EngineRequests.Answer(new EngineReply { Id = "a", Ok = true, Message = "Движки поднимаются." }, Reply);

        var reply = EngineRequests.Answered("a", Reply);

        Assert.NotNull(reply);
        Assert.True(reply.Ok);
        Assert.Equal("Движки поднимаются.", reply.Message);

        // Ответ на прежнюю просьбу следующей не достаётся.
        Assert.Null(EngineRequests.Answered("b", Reply));
    }

    [Fact]
    public async Task Nobody_home_means_not_taken_and_the_request_is_withdrawn()
    {
        var asked = await EngineRequests.SendAsync(
            EngineVerb.Restart,
            "nz",
            take: TimeSpan.FromMilliseconds(400),
            answer: TimeSpan.FromSeconds(5),
            taken: () => Assert.Fail("просьбу никто не забирал"),
            CancellationToken.None,
            Request,
            Reply);

        Assert.False(asked.Taken);
        Assert.Null(asked.Reply);

        // Не оставлена лежать: окно, открытое следом, движки не тронет.
        Assert.False(File.Exists(Request));
    }

    [Fact]
    public async Task A_window_that_takes_and_answers_is_heard()
    {
        bool told = false;

        // Окно: раз в 50 мс смотрит, забирает, отвечает.
        using var stop = new CancellationTokenSource();

        var window = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (EngineRequests.Take(DateTimeOffset.Now, Request) is { } request)
                {
                    await Task.Delay(100);
                    EngineRequests.Answer(new EngineReply { Id = request.Id, Ok = true, Message = request.Verb + " от " + request.From }, Reply);
                }

                await Task.Delay(50);
            }
        });

        var asked = await EngineRequests.SendAsync(
            EngineVerb.Restart,
            "nz",
            take: TimeSpan.FromSeconds(5),
            answer: TimeSpan.FromSeconds(5),
            taken: () => told = true,
            CancellationToken.None,
            Request,
            Reply);

        stop.Cancel();
        await window;

        Assert.True(asked.Taken);
        Assert.True(told);
        Assert.NotNull(asked.Reply);
        Assert.True(asked.Reply.Ok);
        Assert.Equal("Restart от nz", asked.Reply.Message);
    }
}
