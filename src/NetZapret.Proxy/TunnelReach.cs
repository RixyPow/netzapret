using System.Diagnostics;
using System.Net;
using NetZapret.Subscriptions;

namespace NetZapret.Proxy;

/// <summary>Чем кончилась попытка достать имя через туннель.</summary>
public sealed record TunnelReading
{
    public required string Host { get; init; }

    /// <summary>Ответ получен — неважно какой, важно что дошло.</summary>
    public required bool Reached { get; init; }

    /// <summary>Код ответа, если он был.</summary>
    public int? Status { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Почему не дошло.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Проверяет цель через сам туннель, минуя TUN.
/// </summary>
/// <remarks>
/// <para>
/// Отвечает на вопрос, на который отчёт до сих пор отвечал догадкой.
/// Вердикт «туннель не доставил» ставится по неудаче соединения с адресом
/// fakeip — а это не то же самое, что «сервер подписки не дотянулся». Между
/// нами и туннелем стоит ещё winws2: имя в рукопожатии настоящее, его
/// доменный список совпадает, и рецепт десинка правит ровно тот пакет,
/// который мы отправляем внутрь туннеля.
/// </para>
/// <para>
/// Здесь TUN не поднимается. sing-box запускается с локальным входом, как
/// при проверке серверов подписки, и соединение к нему идёт на
/// <c>127.0.0.1</c> — петлю winws2 не трогает. Наружу же уходит соединение
/// к серверу подписки, чьего имени нет ни в одном доменном списке, так что
/// и там рецепт не сработает.
/// </para>
/// <para>
/// Отсюда и различающая сила. Если имя доходит здесь и не доходит через TUN,
/// виноват не сервер, а мы сами. Если не доходит и здесь — дело в сервере
/// либо в самом сайте, и совет сменить сервер наконец обоснован.
/// </para>
/// <para>
/// Прав администратора не нужно: ни драйвера, ни адаптера, ни правки
/// маршрутов. Это тоже важно — проверку зовут из меню, которое человек мог
/// открыть без повышения.
/// </para>
/// </remarks>
public static class TunnelReach
{
    /// <summary>Сколько ждать ответа от одной цели.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Пробует достать каждое имя через выбранный сервер.
    /// </summary>
    /// <param name="singBoxPath">Путь к движку.</param>
    /// <param name="server">Сервер подписки, через который проверяем.</param>
    /// <param name="hosts">Имена, о которых отчёт сказал «туннель не доставил».</param>
    /// <param name="workDirectory">Куда класть временный конфиг и журнал.</param>
    /// <param name="listenPort">Порт локального входа.</param>
    public static async Task<IReadOnlyList<TunnelReading>> CheckAsync(
        string singBoxPath,
        ProxyServer server,
        IReadOnlyList<string> hosts,
        string workDirectory,
        int listenPort,
        CancellationToken cancellationToken)
    {
        if (hosts.Count == 0)
            return [];

        Directory.CreateDirectory(workDirectory);

        var configPath = Path.Combine(workDirectory, "reach.json");
        var logPath = Path.Combine(workDirectory, "reach.log");

        if (File.Exists(logPath))
            File.Delete(logPath);

        // Имя сервера разрешается заранее и честным резолвером. При работающих
        // движках обычный запрос перехватывает наш же TUN, и проверка падала бы
        // на разрешении имени, ни разу не дойдя до сервера, — притом что сервер
        // жив. Ровно эта беда уже ловилась в проверке подписки.
        string? address;

        using (var lookup = new HttpClient { Timeout = Timeout })
        {
            address = await DohResolver.ResolveAsync(server.Host, lookup, cancellationToken);
        }

        var json = new SingBoxConfigCompiler()
            .CompileProbeConfig(server, listenPort, logPath, "warn", address);

        SingBoxConfigCompiler.WriteToFile(configPath, json);

        await using var runner = new SingBoxRunner(singBoxPath, configPath);

        bool ready;

        try
        {
            ready = await runner.StartAsync(listenPort, TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Unavailable(hosts, "движок не успел подняться");
        }

        if (!ready)
        {
            return Unavailable(hosts, runner.ExitCode is { } code
                ? $"sing-box завершился с кодом {code}"
                : "локальный вход не начал принимать соединения");
        }

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{listenPort}"),
            UseProxy = true,

            // Перенаправления не нужны: вопрос в том, дошли ли мы до хоста,
            // а не в том, куда он потом отправит. Лишний переход к тому же
            // добавил бы второе имя, о котором мы не спрашивали.
            AllowAutoRedirect = false,
        };

        using var http = new HttpClient(handler) { Timeout = Timeout };
        var results = new List<TunnelReading>();

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ReachAsync(http, host, cancellationToken));
        }

        return results;
    }

    private static async Task<TunnelReading> ReachAsync(
        HttpClient http,
        string host,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Любой ответ означает, что туннель довёз. Даже 403: он приходит
            // от сайта, а чтобы прийти, ему нужно было сначала нас услышать.
            // Различать «дошло» и «понравилось» здесь незачем — второе
            // выясняет обычная проба.
            return new TunnelReading
            {
                Host = host,
                Reached = true,
                Status = (int)response.StatusCode,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new TunnelReading
            {
                Host = host,
                Reached = false,
                Elapsed = stopwatch.Elapsed,
                Detail = Describe(ex),
            };
        }
    }

    /// <summary>Отказ на человеческом языке.</summary>
    private static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or TaskCanceledException)
                return $"нет ответа за {Timeout.TotalSeconds:0} с";
        }

        return ex.GetBaseException().Message;
    }

    /// <summary>
    /// Движок не поднялся — сказать об этом, а не выдать за отказ целей.
    /// </summary>
    /// <remarks>
    /// Различие существенное. Не поднявшийся движок означает, что проверка
    /// не состоялась вовсе; выдать это за «через туннель тоже не работает»
    /// значило бы подтвердить приговор замером, которого не было.
    /// </remarks>
    private static IReadOnlyList<TunnelReading> Unavailable(IReadOnlyList<string> hosts, string why) =>
        hosts.Select(host => new TunnelReading
        {
            Host = host,
            Reached = false,
            Detail = "проверить не удалось: " + why,
        }).ToList();
}
