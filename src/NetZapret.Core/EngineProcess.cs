using System.Diagnostics;

namespace NetZapret.Core;

/// <summary>
/// Запущенный движок глазами надзора и замера серверов: то немногое от
/// процесса, что им нужно.
/// </summary>
/// <remarks>
/// На Windows каждый движок — отдельный процесс, и это <see cref="OsEngineProcess"/>.
/// На Android sing-box живёт внутри приложения (libbox), а nfqws2 поднимается
/// через su: ни то ни другое не <see cref="Process"/>, который надзор запустил
/// бы сам. Приложение подставляет свой запуск в <see cref="EngineLauncher.Start"/>.
/// Лежит в Core, а не в Supervisor: замер серверов (SingBoxRunner в Proxy)
/// поднимает sing-box тем же путём.
/// </remarks>
public interface IEngineProcess : IDisposable
{
    /// <summary>Номер процесса — для журнала, «Диагностики» и замера нагрузки.</summary>
    int Id { get; }

    bool HasExited { get; }

    /// <summary>Код выхода; читается только после <see cref="HasExited"/>.</summary>
    int ExitCode { get; }

    /// <summary>Вывод движка; обязан вычитываться, иначе процесс встанет на записи.</summary>
    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    void Kill(bool entireProcessTree);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Движок — обычный процесс ОС.</summary>
public sealed class OsEngineProcess(Process process) : IEngineProcess
{
    public Process Process { get; } = process;

    public int Id => Process.Id;

    public bool HasExited => Process.HasExited;

    public int ExitCode => Process.ExitCode;

    public StreamReader StandardOutput => Process.StandardOutput;

    public StreamReader StandardError => Process.StandardError;

    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);

    public Task WaitForExitAsync(CancellationToken cancellationToken) => Process.WaitForExitAsync(cancellationToken);

    public void Dispose() => Process.Dispose();
}

/// <summary>Как поднимается движок по описанию запуска.</summary>
public static class EngineLauncher
{
    /// <summary>
    /// Запуск движка; <c>null</c> — не запустился. По умолчанию — процесс ОС.
    /// </summary>
    /// <remarks>
    /// Первым передаётся тот, кто запускает: служба надзора (SingBoxService,
    /// WinwsService, TgWsProxyService) или SingBoxRunner замера серверов, —
    /// чтобы приложение Android отличало sing-box (libbox в своём процессе)
    /// от winws2 (nfqws2 через su) по типу, а не по имени файла.
    /// </remarks>
    public static Func<object, ProcessStartInfo, IEngineProcess?> Start { get; set; } = StartProcess;

    /// <summary>Запуск процессом ОС — то, что делалось всегда.</summary>
    public static IEngineProcess? StartProcess(object owner, ProcessStartInfo startInfo) =>
        Process.Start(startInfo) is { } process ? new OsEngineProcess(process) : null;
}
