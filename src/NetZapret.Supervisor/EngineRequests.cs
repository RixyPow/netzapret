using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetZapret.Supervisor;

/// <summary>Что просят сделать с движками.</summary>
public enum EngineVerb
{
    Start,
    Stop,
    Restart,
}

/// <summary>Просьба к окну о движках.</summary>
public sealed record EngineRequest
{
    public required string Id { get; init; }

    public required EngineVerb Verb { get; init; }

    /// <summary>Кто просит — уходит в журнал окна.</summary>
    public required string From { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>Ответ окна на просьбу.</summary>
public sealed record EngineReply
{
    public required string Id { get; init; }

    public required bool Ok { get; init; }

    public required string Message { get; init; }
}

/// <summary>Чем кончилась просьба для просившего.</summary>
/// <param name="Taken">Окно просьбу забрало.</param>
/// <param name="Reply">Что окно ответило; <c>null</c> — не ответило в срок.</param>
public sealed record EngineAsked(bool Taken, EngineReply? Reply);

/// <summary>
/// Просьба к открытому окну — запустить, погасить или перезапустить движки — файлом в <c>runtime\</c>.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 11.11. <c>nz</c> прав администратора не просит намеренно, а движкам они нужны:
/// поднять и погасить их могло только окно — кнопкой либо ключами <c>--start</c> и <c>--stop</c>,
/// то есть через UAC. Проверка правки пресета или модуля lua вживую упиралась в человека:
/// вечером 10.11 каждую из трёх правок модуля владельцу пришлось запускать руками. Теперь
/// <c>nz</c> оставляет просьбу, а окно, у которого права есть, делает то же, что его кнопка
/// (<c>EngineControl</c>), и отвечает теми же словами.
/// </para>
/// <para>
/// Файлом, а не именованным событием, как <see cref="SupervisorStop"/>: объекты окна созданы
/// от администратора, и программе без прав их не поднять, а просьбе нужен ещё и ответ словами.
/// </para>
/// <para>
/// В просьбе только глагол — ни путей, ни ключей: окно запускает движки со своими настройками.
/// Новых прав она не даёт: кто может писать в папку программы, тот и так правит её настройки,
/// пресеты и модули, которые движки прочтут при следующем запуске.
/// </para>
/// </remarks>
public static class EngineRequests
{
    public static string RequestPath => Path.Combine("runtime", "engines.request.json");

    public static string ReplyPath => Path.Combine("runtime", "engines.reply.json");

    /// <summary>
    /// Просьба старше этого не исполняется.
    /// </summary>
    /// <remarks>
    /// Окно смотрит раз в секунду, так что свежая просьба лежит недолго. Старая — значит,
    /// окна тогда не было: исполнить её при следующем запуске значило бы погасить движки
    /// через час после того, как человек об этом и думать забыл.
    /// </remarks>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(20);

    /// <summary>Сколько знаков имени просившего уходит в журнал окна.</summary>
    private const int FromLimit = 24;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Оставляет просьбу; прежняя, которую окно не забрало, заменяется.</summary>
    public static EngineRequest Ask(EngineVerb verb, string from, DateTimeOffset now, string? path = null)
    {
        var request = new EngineRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Verb = verb,
            From = from,
            At = now,
        };

        Write(path ?? RequestPath, JsonSerializer.Serialize(request, Options));

        return request;
    }

    /// <summary>
    /// Забирает просьбу: файл удаляется, чтобы она не исполнилась дважды.
    /// </summary>
    /// <returns>
    /// <c>null</c> — просьбы нет, она не читается, устарела либо файл не удалился (тогда она
    /// осталась бы лежать и исполнялась бы на каждом круге).
    /// </returns>
    public static EngineRequest? Take(DateTimeOffset now, string? path = null)
    {
        path ??= RequestPath;

        if (!File.Exists(path))
            return null;

        EngineRequest? request = null;

        try
        {
            request = JsonSerializer.Deserialize<EngineRequest>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Не просьба — мусор: убрать и забыть.
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (SupervisorState.IsBusy(ex))
        {
            return null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Id) || !Enum.IsDefined(request.Verb))
            return null;

        // Из будущего — тоже не свежая: часы у просившего и у окна одни.
        var age = now - request.At;

        if (age > Fresh || age < -Fresh)
            return null;

        return request with { From = Plain(request.From) };
    }

    /// <summary>Лежит ли ещё просьба с этим номером — окно её не забрало.</summary>
    public static bool Pending(string id, string? path = null)
    {
        path ??= RequestPath;

        try
        {
            return File.Exists(path)
                && JsonSerializer.Deserialize<EngineRequest>(File.ReadAllText(path), Options)?.Id == id;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Файл в этот миг забирают или заменяют — значит, уже не лежит нетронутым.
            return false;
        }
    }

    /// <summary>Убирает свою просьбу, которую никто не забрал; чужую не трогает.</summary>
    public static void Withdraw(string id, string? path = null)
    {
        path ??= RequestPath;

        if (!Pending(id, path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (SupervisorState.IsBusy(ex))
        {
            // Устареет сама: Take её не исполнит.
        }
    }

    /// <summary>Ответ окна — просившему.</summary>
    public static void Answer(EngineReply reply, string? path = null) =>
        Write(path ?? ReplyPath, JsonSerializer.Serialize(reply, Options));

    /// <summary>Ответ на просьбу с этим номером; <c>null</c> — его ещё нет.</summary>
    public static EngineReply? Answered(string id, string? path = null)
    {
        path ??= ReplyPath;

        try
        {
            if (!File.Exists(path))
                return null;

            var reply = JsonSerializer.Deserialize<EngineReply>(File.ReadAllText(path), Options);

            return reply?.Id == id ? reply : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Оставляет просьбу и ждёт: сперва — что окно её заберёт, потом — что ответит.
    /// </summary>
    /// <param name="take">Сколько ждать, пока окно заберёт просьбу; не забрало — она убирается.</param>
    /// <param name="answer">Сколько ждать ответа: перезапуск — это остановка, пауза и сборка конфига.</param>
    /// <param name="taken">Зовётся, когда окно просьбу забрало.</param>
    public static async Task<EngineAsked> SendAsync(
        EngineVerb verb,
        string from,
        TimeSpan take,
        TimeSpan answer,
        Action? taken,
        CancellationToken cancellationToken,
        string? requestPath = null,
        string? replyPath = null)
    {
        var step = TimeSpan.FromMilliseconds(200);
        var request = Ask(verb, from, DateTimeOffset.Now, requestPath);

        for (var waited = TimeSpan.Zero; Pending(request.Id, requestPath); waited += step)
        {
            if (waited >= take)
            {
                Withdraw(request.Id, requestPath);
                return new EngineAsked(false, null);
            }

            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }

        taken?.Invoke();

        for (var waited = TimeSpan.Zero; waited < answer; waited += step)
        {
            if (Answered(request.Id, replyPath) is { } reply)
                return new EngineAsked(true, reply);

            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }

        return new EngineAsked(true, Answered(request.Id, replyPath));
    }

    /// <summary>
    /// Ждёт, пока движки поднимутся (<see cref="EngineHealth.Running"/>) либо пока надзор уйдёт.
    /// </summary>
    /// <param name="up"><c>true</c> — ждать поднятых движков, <c>false</c> — остановленных.</param>
    public static async Task<bool> WaitAsync(bool up, TimeSpan limit, CancellationToken cancellationToken, string? statePath = null)
    {
        var step = TimeSpan.FromMilliseconds(500);

        for (var waited = TimeSpan.Zero; ; waited += step)
        {
            var state = SupervisorState.Load(statePath ?? SupervisorState.DefaultPath);
            bool there = up ? EngineHealth.Running(state) : state is null || !state.IsSupervisorAlive();

            if (there)
                return true;

            if (waited >= limit)
                return false;

            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Имя просившего для журнала: одна строка, без управляющих знаков, не длиннее <see cref="FromLimit"/>.
    /// </summary>
    /// <remarks>Файл просьбы пишет кто угодно — в журнал окна идёт только безобидное.</remarks>
    private static string Plain(string? from)
    {
        var plain = new string((from ?? string.Empty).Where(c => !char.IsControl(c)).Take(FromLimit).ToArray()).Trim();

        return plain.Length == 0 ? "без имени" : plain;
    }

    /// <summary>Запись через временный файл: читающий не должен застать половину.</summary>
    private static void Write(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));

        // Тот же короткий повтор, что у файла состояния: вторая сторона читает
        // файл раз в долю секунды и в этот миг держит его.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (SupervisorState.IsBusy(ex) && attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
