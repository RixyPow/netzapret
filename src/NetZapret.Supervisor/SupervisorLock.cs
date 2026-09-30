using System.Diagnostics;
using System.Text;

namespace NetZapret.Supervisor;

/// <summary>
/// Замок супервизора: файл, открытый на запись всё время его жизни.
/// </summary>
/// <remarks>
/// <para>
/// Прежде о живом супервизоре знал только файл состояния, а он пишется,
/// когда движки уже подняты, — до двадцати секунд после старта. В это
/// окно второй супервизор прежнего не видел. Отчёт reaass, 01.10: два
/// «Запустить» подряд подняли два супервизора с разницей в секунду, второй
/// снял движки первого как «без хозяина», и они гасили друг друга до «сдаюсь».
/// </para>
/// <para>
/// Файл, а не именованный мьютекс: мьютекс принадлежит потоку, а супервизор
/// живёт в асинхронном коде, где продолжение уходит на другой поток. Файл
/// держит процесс, и умерший процесс отпускает его сам — не остаётся ни
/// «брошенного» замка, ни ручной уборки. Внутри — номер процесса: держатель
/// разрешает читать, и сменщик узнаёт, кого снимать.
/// </para>
/// </remarks>
public sealed class SupervisorLock : IDisposable
{
    private readonly FileStream _file;

    private SupervisorLock(FileStream file) => _file = file;

    public static string DefaultPath => Path.Combine("runtime", "supervisor.lock");

    /// <summary>Берёт замок; занят — <c>null</c>.</summary>
    public static SupervisorLock? TryAcquire(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var file = new FileStream(target, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            try
            {
                var number = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
                file.SetLength(0);
                file.Write(number);
                file.Flush(flushToDisk: true);
            }
            catch
            {
                file.Dispose();
                throw;
            }

            return new SupervisorLock(file);
        }
        catch (Exception ex) when (SupervisorState.IsBusy(ex))
        {
            return null;
        }
    }

    /// <summary>Номер процесса, держащего замок; никто не держит — <c>null</c>.</summary>
    public static int? Holder(string? path = null)
    {
        var target = path ?? DefaultPath;

        if (!File.Exists(target))
            return null;

        try
        {
            // Открылся на запись — значит, не держит никто: номер внутри от умершего.
            using var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return null;
        }
        catch (Exception ex) when (SupervisorState.IsBusy(ex))
        {
            // Держат — читаем, кто.
        }

        try
        {
            using var read = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(read, Encoding.ASCII);

            return int.TryParse(reader.ReadToEnd().Trim(), out int id) ? id : null;
        }
        catch (Exception ex) when (SupervisorState.IsBusy(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Жив ли процесс и тот ли это exe, что у нас.
    /// </summary>
    /// <remarks>
    /// Номер процесса Windows отдаёт заново: по старому номеру из файла
    /// можно снять чужую программу. Имя супервизора то же, что у окна, —
    /// это и проверяется.
    /// </remarks>
    public static bool IsOurs(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            using var self = Process.GetCurrentProcess();

            return !process.HasExited
                && string.Equals(process.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _file.Dispose();
}
