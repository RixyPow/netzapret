using System.ComponentModel;
using System.Diagnostics;

namespace NetZapret.Android.Host;

/// <summary>
/// Root телефона: есть ли он и выполнить сценарий от его имени.
/// </summary>
/// <remarks>
/// <para>
/// Root нужен только nfqws2 (десинк пакетами, как winws2 на Windows); без него
/// десинк делает ByeDPI, и всё главное работает (требование владельца).
/// Проверка — <c>su -c id</c>: менеджер root (Magisk) показывает окно
/// «разрешить?» и ждёт человека, поэтому срок — 20 с, а не секунда. KernelSU
/// и APatch неразрешённому приложению молча отказывают.
/// </para>
/// <para>
/// Ответ запоминается на весь процесс: окно Magisk на каждом «Запустить»
/// было бы пыткой, а отказ человек меняет в самом менеджере — после этого
/// приложение перезапускают.
/// </para>
/// </remarks>
internal static class RootShell
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool? _known;

    /// <summary>Что известно без вопроса; null — ещё не спрашивали.</summary>
    public static bool? Known => _known;

    public static async Task<bool> HasRootAsync(CancellationToken cancellationToken)
    {
        if (_known is { } known)
            return known;

        await Gate.WaitAsync(cancellationToken);

        try
        {
            if (_known is { } again)
                return again;

            var result = await RunAsync("id", TimeSpan.FromSeconds(20), cancellationToken);
            _known = result is { ExitCode: 0 } && result.Output.Contains("uid=0", StringComparison.Ordinal);
            return _known.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Итог сценария: код выхода и весь вывод (stdout и stderr вместе).</summary>
    public sealed record Outcome(int ExitCode, string Output);

    /// <summary>
    /// Выполнить сценарий через <c>su -c</c>; null — su нет или он не ответил в срок.
    /// </summary>
    /// <remarks>
    /// Текст сценария в журнал не пишется: секретов в наших сценариях нет,
    /// но правило одно для всех (CLAUDE.md, «Секреты»).
    /// </remarks>
    public static async Task<Outcome?> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("su")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);

        Process? process;

        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            // su нет — root нет.
            return null;
        }

        if (process is null)
            return null;

        using (process)
        {
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            return new Outcome(process.ExitCode, await output + await errors);
        }
    }
}
