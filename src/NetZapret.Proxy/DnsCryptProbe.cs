using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Задержка режима dnscrypt-proxy или почему её нет.</summary>
public sealed record DnsCryptLatency(DnsCryptMode Mode, double? Ms, string Failure);

/// <summary>
/// Меряет шифрованные резолверы — dnscrypt-proxy в каждом режиме.
/// </summary>
/// <remarks>
/// <para>
/// Обзор резолверов (<see cref="DnsSurvey"/>) меряет адреса DoH, а шифрованные —
/// это dnscrypt-proxy на петле, свой движок: до 05.11 их строка на вкладке DNS
/// после обзора навсегда оставалась «…» (обзор ставил «…» всем, а заполнял
/// только найденные по адресу DoH).
/// </para>
/// <para>
/// Поэтому так: работающий режим спрашивается там, где он слушает; прочие —
/// временным dnscrypt-proxy на свободном порту петли, со своей папкой и без кэша,
/// и гасятся сразу после замера. Строки готовности не ждём: замер 05.11
/// (2.1.20, движки подняты) — первый живой сервер через 0,7–3,6 с, ответы
/// с 1–5-й секунды, а строку «live servers» ODoH не написал и за 45 с — он
/// обходит все серверы с повторами. Задержка — середина трёх ответов на разные
/// имена (кэш выключен): DNSCrypt 25–190 мс, анонимный 78–555, ODoH 61–885.
/// </para>
/// </remarks>
public static class DnsCryptProbe
{
    /// <summary>Сколько ждать первого ответа от только что поднятого dnscrypt-proxy.</summary>
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(15);

    /// <summary>Срок одного вопроса.</summary>
    private static readonly TimeSpan Ask = TimeSpan.FromSeconds(3);

    private static readonly string[] Names = ["www.google.com", "www.wikipedia.org", "github.com"];

    /// <summary>Папка временных замеров — рядом с рабочей, чтобы не трогать конфиг работающего.</summary>
    public static string ProbeDirectory => Path.Combine("runtime", "dnscrypt-probe");

    /// <summary>Меряет все режимы разом.</summary>
    /// <param name="active">Режим, который сейчас работает у движка: его спрашиваем на месте.</param>
    public static Task<DnsCryptLatency[]> MeasureAllAsync(DnsCryptMode? active, CancellationToken cancellationToken) =>
        Task.WhenAll(DnsCrypt.Modes.Select(mode => MeasureAsync(mode, mode == active, cancellationToken)));

    public static async Task<DnsCryptLatency> MeasureAsync(DnsCryptMode mode, bool running, CancellationToken cancellationToken)
    {
        // Работающий — на его же порту. Если вдруг не ответил (движки гасли),
        // меряем временным, как прочие.
        if (running && await TimeAsync(DnsCrypt.Port, cancellationToken) is { Ms: not null } live)
            return live with { Mode = mode };

        var executable = DnsCrypt.Executable();

        if (!File.Exists(executable))
            return new(mode, null, "dnscrypt-proxy не найден");

        var directory = Path.GetFullPath(Path.Combine(ProbeDirectory, DnsCrypt.Key(mode)));
        int port = FreePort();

        try
        {
            // Списки — сперва те, что обновил работающий (свежее), потом из поставки.
            DnsCrypt.SeedLists(mode, DnsCrypt.RuntimeDirectory, directory);
            DnsCrypt.SeedLists(mode, DnsCrypt.ShippedDirectory(), directory);

            var config = Path.Combine(directory, "dnscrypt-proxy.toml");
            File.WriteAllText(config, DnsCrypt.Config(mode, directory, port, cache: false), new UTF8Encoding(false));

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            start.ArgumentList.Add("-config");
            start.ArgumentList.Add(config);

            using var process = Process.Start(start);

            if (process is null)
                return new(mode, null, "dnscrypt-proxy не запустился");

            // Вывод читать надо: иначе полный канал остановит процесс.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var clock = Stopwatch.StartNew();

                while (clock.Elapsed < Warmup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (process.HasExited)
                        return new(mode, null, "dnscrypt-proxy завершился сам");

                    if ((await AskAsync(port, "example.org", cancellationToken)).Ms is not null)
                        return (await TimeAsync(port, cancellationToken)) with { Mode = mode };

                    await Task.Delay(500, cancellationToken);
                }

                return new(mode, null, $"ни один сервер не ответил за {Warmup.TotalSeconds:0} с");
            }
            finally
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Уже вышел.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(mode, null, ex.Message);
        }
    }

    /// <summary>Середина трёх ответов на разные имена; не ответило ни одно — причина.</summary>
    private static async Task<DnsCryptLatency> TimeAsync(int port, CancellationToken cancellationToken)
    {
        var times = new List<double>();

        foreach (var name in Names)
        {
            if ((await AskAsync(port, name, cancellationToken)).Ms is { } ms)
                times.Add(ms);
        }

        if (times.Count == 0)
            return new(default, null, "не отвечает");

        times.Sort();

        return new(default, times[times.Count / 2], string.Empty);
    }

    /// <summary>Один вопрос по UDP к петле.</summary>
    internal static async Task<(double? Ms, DnsWire.Answer? Answer)> AskAsync(int port, string name, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Ask);

        var buffer = new byte[4096];
        var watch = Stopwatch.StartNew();

        try
        {
            await socket.SendToAsync(DnsWire.Query(name, 1), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port), limit.Token);
            var got = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), limit.Token);
            var answer = DnsWire.Parse(buffer.AsSpan(0, got.ReceivedBytes));

            // Ответ без адресов — не замер: dnscrypt-proxy ещё без серверов отвечает SERVFAIL.
            return answer is { Code: 0, Addresses.Count: > 0 } ? (watch.Elapsed.TotalMilliseconds, answer) : (null, answer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (SocketException)
        {
            // Порт ещё не слушают — ICMP «недоступен» приходит сбросом.
            return (null, null);
        }
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
