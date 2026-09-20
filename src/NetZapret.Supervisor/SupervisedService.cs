using System.Diagnostics;
using System.Text;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>
/// Чем кончилась функциональная проверка службы.
/// </summary>
/// <remarks>
/// Главное здесь — что <see cref="UpstreamDown"/> отделён от
/// <see cref="Broken"/>. Снаружи оба выглядят одинаково: трафик не идёт.
/// Но лечатся они противоположным, и перезапуск во втором случае — чистый
/// вред: движок гаснет на восемнадцать секунд и поднимается в ту же
/// мёртвую подписку.
/// </remarks>
public enum ServiceCheck
{
    /// <summary>Работает.</summary>
    Healthy,

    /// <summary>Сломан сам движок — перезапуск уместен.</summary>
    Broken,

    /// <summary>
    /// Движок исправен, но наружу не доходит: выход мёртв.
    /// </summary>
    /// <remarks>
    /// Распознаётся по тому, что Clash API движка отвечает. Отвечает —
    /// значит процесс жив, слушает и обрабатывает запросы; всё, что
    /// не доходит дальше, лежит за пределами нашей власти.
    /// </remarks>
    UpstreamDown,
}

public enum ServiceHealth
{
    /// <summary>Не запускался или остановлен намеренно.</summary>
    Stopped,

    /// <summary>Процесс жив и функциональная проверка пройдена.</summary>
    Healthy,

    /// <summary>Процесс жив, но функциональная проверка не проходит.</summary>
    Degraded,

    /// <summary>Процесс умер.</summary>
    Dead,

    /// <summary>Перезапуски исчерпаны, служба сдалась.</summary>
    Faulted,
}

/// <summary>
/// Служба под присмотром супервизора.
/// </summary>
/// <remarks>
/// Проверка живости двухуровневая намеренно. «Процесс существует» — слабая
/// гарантия: sing-box успевает подняться и умереть по существу, оставаясь
/// живым процессом. Поэтому у каждой службы есть ещё и функциональная
/// проверка, своя для каждой.
/// </remarks>
public abstract class SupervisedService
{
    public abstract string Name { get; }

    protected Process? Process { get; set; }

    /// <summary>
    /// Куда складывать вывод службы. Если не задан, вывод отбрасывается,
    /// но всё равно вычитывается.
    /// </summary>
    public string? OutputLogPath
    {
        get => _outputLogPath;
        set
        {
            _outputLogPath = value;
            _log?.Dispose();
            _log = value is null ? null : new RollingLog(value);
        }
    }

    private string? _outputLogPath;
    private RollingLog? _log;

    public int? ProcessId => Process is { HasExited: false } ? Process.Id : null;

    public DateTimeOffset? StartedAt { get; private set; }

    public int RestartCount { get; private set; }

    public string? LastError { get; protected set; }

    /// <summary>Процесс существует и не завершился.</summary>
    public bool IsProcessAlive => Process is { HasExited: false };

    /// <summary>
    /// Функциональная проверка: слушается ли порт, отвечает ли API.
    /// Для служб без наблюдаемого признака возвращает <see cref="ServiceCheck.Healthy"/>.
    /// </summary>
    /// <remarks>
    /// Исходов три, а не два, и третий добавлен по замеру 20.09: у владельца
    /// семь из девяти серверов подписки перестали отвечать, проверка трафика
    /// честно проваливалась, и супервизор перезапускал исправный движок.
    /// Восемнадцать секунд тишины, потом снова — это и есть жалоба «туннель
    /// отваливается на пару секунд». Перезапуск не чинил ничего: мёртвые
    /// серверы подписки от него не оживают.
    /// </remarks>
    public abstract Task<ServiceCheck> CheckFunctionalAsync(CancellationToken cancellationToken);

    protected abstract ProcessStartInfo BuildStartInfo();

    /// <summary>Проверяет, что всё нужное для запуска на месте.</summary>
    public abstract string? ValidatePrerequisites();

    /// <summary>
    /// Задание, в которое включаются запущенные процессы, чтобы они
    /// не пережили супервизор.
    /// </summary>
    public ProcessJob? Job { get; set; }

    /// <summary>Имена процессов движка — для поиска осиротевших экземпляров.</summary>
    public abstract IReadOnlyList<string> EngineProcessNames { get; }

    public async Task<bool> StartAsync(TimeSpan readinessTimeout, CancellationToken cancellationToken)
    {
        var problem = ValidatePrerequisites();

        if (problem is not null)
        {
            LastError = problem;
            return false;
        }

        try
        {
            Process = Process.Start(BuildStartInfo());
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            return false;
        }

        if (Process is null)
        {
            LastError = "процесс не запустился";
            return false;
        }

        // Включаем в задание сразу после запуска: если супервизор умрёт
        // аварийно, система погасит движок вместе с ним.
        Job?.Assign(Process);

        // Вывод обязан вычитываться. Перенаправленный и никем не читаемый
        // конвейер заполняется и намертво блокирует дочерний процесс на записи
        // в stdout — служба при этом выглядит живой и проходит проверку порта.
        StartOutputPump(Process.StandardOutput);
        StartOutputPump(Process.StandardError);

        StartedAt = DateTimeOffset.Now;

        var deadline = DateTime.UtcNow + readinessTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive)
            {
                LastError = $"процесс завершился с кодом {Process.ExitCode}";
                return false;
            }

            // На старте годится и мёртвый выход: движок поднялся, а чинить
            // чужие серверы не его забота. Отказать здесь значило бы вовсе
            // не дать туннелю встать, пока подписка не оживёт.
            if (await CheckFunctionalAsync(cancellationToken) is not ServiceCheck.Broken)
            {
                LastError = null;
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        LastError = "функциональная проверка не прошла за отведённое время";
        return false;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Process is null || Process.HasExited)
        {
            Process = null;
            StartedAt = null;
            return;
        }

        try
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception)
        {
            // Мог завершиться сам между проверкой и вызовом.
        }
        finally
        {
            Process?.Dispose();
            Process = null;
            StartedAt = null;
        }
    }

    internal void NoteRestart() => RestartCount++;

    private void StartOutputPump(StreamReader reader)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    RecordOutput(line);
            }
            catch (Exception)
            {
                // Поток закрывается вместе с процессом — это штатное завершение насоса.
            }
        });
    }

    /// <summary>
    /// Пишет в журнал движка строку от самого супервизора.
    /// </summary>
    /// <remarks>
    /// Нужна, когда супервизор принимает решение молча: без отметки в журнале
    /// отличить «проверка пройдена» от «проверка пропущена» можно только по
    /// исходникам, а ищут причину обычно как раз по журналу.
    /// </remarks>
    protected void Note(string line) => RecordOutput($"[супервизор] {line}");

    /// <remarks>
    /// Зовётся на каждую строку вывода движка, а winws2 пишет строку
    /// на соединение — то есть это горячий путь, и лишней работы здесь
    /// быть не должно.
    ///
    /// Её тут и было: строки складывались в очередь на сотню, под замком,
    /// с подрезкой на каждой записи. Читать эту очередь было некому —
    /// свойство, ради которого она велась, не вызывалось ни разу
    /// ни в консоли, ни в окне, ни в проверках.
    /// </remarks>
    private void RecordOutput(string line) => _log?.AppendLine(line);
}

/// <summary>
/// Прокси-движок sing-box.
/// </summary>
/// <remarks>
/// Функциональная проверка — доступность Clash API. Порт указан в самом
/// конфиге, так что проверка работает одинаково и для TUN-конфига,
/// и для проверочного с локальным инбаундом.
/// </remarks>
public sealed class SingBoxService : SupervisedService
{
    private readonly string _executablePath;
    private readonly string _configPath;
    private readonly int _healthPort;
    private readonly int? _trafficPort;
    private readonly int _trafficCheckEvery;

    private int _checkCounter;
    private bool _lastTrafficOk = true;
    private DateTime? _trafficPortMissingSince;
    private bool _trafficPortMissingNoted;
    private bool _upstreamNoted;

    /// <summary>
    /// Сколько ждать порт проверки, прежде чем счесть, что его нет в конфиге.
    /// </summary>
    /// <remarks>
    /// Clash API и вход проверки открываются независимо, и на старте один
    /// успевает раньше другого. Без выдержки первый же опрос принял бы
    /// неподнявшийся порт за отсутствующий. Держится заметно короче таймаута
    /// готовности, иначе выдержка не успела бы истечь и отказ выглядел бы
    /// по-старому — молчаливым.
    /// </remarks>
    private static readonly TimeSpan TrafficPortGrace = TimeSpan.FromSeconds(5);

    /// <param name="healthPort">Порт Clash API — быстрая проверка живости.</param>
    /// <param name="trafficPort">
    /// Порт локального прокси для глубокой проверки. Если задан, раз в
    /// <paramref name="trafficCheckEvery"/> опросов делается настоящий запрос
    /// через прокси. Слушающий порт означает лишь, что движок поднялся;
    /// он ничего не говорит о том, доходит ли трафик до сервера.
    /// </param>
    public SingBoxService(
        string executablePath,
        string configPath,
        int healthPort = 9090,
        int? trafficPort = null,
        int trafficCheckEvery = 6)
    {
        _executablePath = executablePath;
        _configPath = configPath;
        _healthPort = healthPort;
        _trafficPort = trafficPort;
        _trafficCheckEvery = Math.Max(1, trafficCheckEvery);
    }

    public override string Name => "sing-box";

    public override IReadOnlyList<string> EngineProcessNames => ["sing-box"];

    public override string? ValidatePrerequisites()
    {
        if (!File.Exists(_executablePath))
            return $"движок не найден: {_executablePath}";

        if (!File.Exists(_configPath))
            return $"конфиг не найден: {_configPath}";

        return null;
    }

    protected override ProcessStartInfo BuildStartInfo() => new()
    {
        FileName = _executablePath,
        ArgumentList = { "run", "-c", Path.GetFullPath(_configPath) },
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_executablePath)) ?? ".",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,

        // Кодировка задаётся явно, иначе .NET разбирает вывод движка в кодовой
        // странице консоли — на русской Windows это не UTF-8, и байты имени
        // сервера превращаются в кашу, которую журнал честно записывает
        // обратно. Получалось двойное перекодирование: в файле лежало
        // «рџ‡єрџ‡ё Hysteria2 | РЎРЁРђ» вместо флага и слова «США».
        //
        // Стало заметно, когда журнал перестал быть свалкой и начал читаться:
        // проверка достаёт оттуда, через какой выход шло имя и что ответила
        // труба, — и имя выхода в отчёте было нечитаемым.
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false),
    };

    public override async Task<ServiceCheck> CheckFunctionalAsync(CancellationToken cancellationToken)
    {
        // Clash API — единственный признак, отделяющий сломанный движок
        // от мёртвой подписки. Не отвечает — процесс жив, но не работает:
        // такое перезапуском и лечится.
        if (!await SingBoxRunner.IsPortAcceptingAsync(_healthPort, TimeSpan.FromSeconds(2), cancellationToken))
            return ServiceCheck.Broken;

        if (_trafficPort is null)
            return ServiceCheck.Healthy;

        // Порт проверки есть не во всяком конфиге: собранный прежней версией
        // или руками, он может не содержать входа вовсе. Настаивать в таком
        // случае нельзя — проверка не пройдёт никогда, и супервизор будет
        // убивать исправный движок, пока не исчерпает попытки перезапуска.
        // Именно так и было: с TUN локального входа не существовало,
        // и рабочий туннель гасился по кругу.
        if (!await SingBoxRunner.IsPortAcceptingAsync(_trafficPort.Value, TimeSpan.FromSeconds(1), cancellationToken))
        {
            _trafficPortMissingSince ??= DateTime.UtcNow;

            if (DateTime.UtcNow - _trafficPortMissingSince < TrafficPortGrace)
                return ServiceCheck.Broken;

            if (!_trafficPortMissingNoted)
            {
                _trafficPortMissingNoted = true;
                Note(
                    $"на порту {_trafficPort} никто не слушает — в конфиге нет входа проверки. " +
                    "Проход трафика не проверяется, движок считается живым по Clash API. " +
                    "Пересоберите конфиг командой config.");
            }

            return ServiceCheck.Healthy;
        }

        _trafficPortMissingSince = null;

        // Глубокая проверка делается редко: она уходит в сеть и стоит секунды.
        // Между проверками используется её последний результат.
        if (_checkCounter++ % _trafficCheckEvery != 0)
            return _lastTrafficOk ? ServiceCheck.Healthy : ServiceCheck.UpstreamDown;

        _lastTrafficOk = await CheckTrafficAsync(_trafficPort.Value, cancellationToken);

        if (_lastTrafficOk)
            return ServiceCheck.Healthy;

        // Движок отвечает по своему API, а наружу не доходит. Перезапуск
        // тут бесполезен и вреден: восемнадцать секунд тишины, и подъём
        // в ту же мёртвую подписку.
        //
        // Замер 20.09 у владельца: семь из девяти серверов не отвечали,
        // в журнале движка подряд «stream error: INTERNAL_ERROR; received
        // from peer» и «report handshake success: connection timed out».
        // Супервизор при этом исправно гасил здоровый движок.
        LastError = "трафик через прокси не проходит — похоже, мёртв выход подписки, а не движок";

        if (!_upstreamNoted)
        {
            _upstreamNoted = true;
            Note(
                "трафик не проходит, но движок отвечает по Clash API. "
                + "Перезапускать его незачем: скорее всего не отвечают серверы подписки. "
                + "Проверьте их в разделе «VPN».");
        }

        return ServiceCheck.UpstreamDown;
    }

    private static async Task<bool> CheckTrafficAsync(int proxyPort, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true,
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var text = await http.GetStringAsync("https://api.ipify.org", cancellationToken);

            return System.Net.IPAddress.TryParse(text.Trim(), out _);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Десинк winws2.
/// </summary>
/// <remarks>
/// <para>
/// Функциональной проверки нет: winws2 не слушает портов и не имеет API —
/// он висит на фильтре WinDivert. Единственный доступный признак — живость
/// процесса, что и отражено возвратом <c>true</c>.
/// </para>
/// <para>
/// ВНИМАНИЕ: не тестировано. Запуск winws2 требует прав администратора
/// (драйвер WinDivert), а ночная сессия работала без них по условию задания.
/// Код написан, но вживую не проверялся — нужен ручной запуск с повышением прав.
/// </para>
/// </remarks>
public sealed class WinwsService : SupervisedService
{
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string _workingDirectory;

    public WinwsService(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
    }

    public override string Name => "winws2";

    public override IReadOnlyList<string> EngineProcessNames => ["winws2", "winws"];

    public override string? ValidatePrerequisites() =>
        File.Exists(_executablePath) ? null : $"движок не найден: {_executablePath}";

    protected override ProcessStartInfo BuildStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // См. выше: без явной кодировки вывод разбирается кодовой страницей
            // консоли, и журнал получает перекодированную дважды кашу вместо
            // текста. Здесь это winws2, и его сообщения тоже читаются.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in _arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    public override Task<ServiceCheck> CheckFunctionalAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsProcessAlive ? ServiceCheck.Healthy : ServiceCheck.Broken);
}
