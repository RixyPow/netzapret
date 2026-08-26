using System.Text;

namespace NetZapret.Supervisor;

/// <summary>
/// Пишет в файл журнала так, чтобы несколько процессов не портили друг друга.
/// </summary>
/// <remarks>
/// <para>
/// Обычный <see cref="StreamWriter"/> поверх файла, открытого на дописывание,
/// с этим не справляется. Позиция записи запоминается при открытии, и два
/// супервизора, открывшие файл одновременно, пишут поверх написанного соседом.
/// Выглядит это так:
/// </para>
/// <code>
/// не запустился — про■2026-08-26 14:13:50 sing-box: не запус■14:13:51 winws2: запуск
/// </code>
/// <para>
/// Цена такой порчи оказалась выше, чем кажется: при разборе утреннего сбоя
/// пропало ровно то сообщение, которое объясняло, почему движок не поднялся.
/// Журнал, которому нельзя верить, хуже отсутствующего — он выглядит уликой,
/// оставаясь мусором.
/// </para>
/// <para>
/// Здесь запись идёт под общесистемным мьютексом, и перед каждой записью
/// позиция переставляется в конец. Пишем редко, несколько строк в минуту,
/// так что накладные расходы значения не имеют.
/// </para>
/// </remarks>
public sealed class SharedLogWriter : TextWriter
{
    private readonly string _path;
    private readonly Mutex _mutex;
    private readonly object _local = new();

    public override Encoding Encoding => Encoding.UTF8;

    private SharedLogWriter(string path, Mutex mutex)
    {
        _path = path;
        _mutex = mutex;
    }

    /// <summary>
    /// Открывает журнал; <c>null</c>, если это невозможно.
    /// </summary>
    /// <remarks>
    /// Отказ не считается ошибкой запуска: движки важнее записи о них.
    /// </remarks>
    public static SharedLogWriter? TryOpen(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Имя мьютекса выводится из пути: два журнала в разных местах
            // друг друга не касаются, а один и тот же — общий для всех
            // процессов, включая запущенные от администратора.
            var name = "Global\\netzapret-log-" + Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(
                    Encoding.UTF8.GetBytes(full.ToLowerInvariant())));

            var mutex = new Mutex(false, name);
            var writer = new SharedLogWriter(full, mutex);

            writer.Trim();
            return writer;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void Write(char value) => Write(value.ToString());

    public override void WriteLine(string? value) => Write((value ?? string.Empty) + Environment.NewLine);

    public override void WriteLine() => Write(Environment.NewLine);

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        lock (_local)
        {
            bool held = false;

            try
            {
                // Брошенный мьютекс означает, что писавший процесс умер, не
                // отпустив его. Владение всё равно переходит к нам, и это
                // ровно то, что нужно: иначе один упавший супервизор запер бы
                // журнал до перезагрузки.
                try
                {
                    held = _mutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    held = true;
                }

                using var stream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);

                // Несмотря на FileMode.Append: под мьютексом это дёшево,
                // а полагаться на то, что позиция уже верна, здесь нельзя —
                // именно это предположение и портило файл.
                stream.Seek(0, SeekOrigin.End);

                var bytes = Encoding.UTF8.GetBytes(value);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                // Потеря строки журнала не должна ронять то, о чём она.
            }
            finally
            {
                if (held)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }
            }
        }
    }

    private const int SizeLimit = 512 * 1024;
    private const int KeepLines = 500;

    /// <summary>
    /// Укорачивает разросшийся журнал, оставляя хвост.
    /// </summary>
    /// <remarks>
    /// Под тем же мьютексом: перезапись файла целиком, пока в него пишет
    /// другой процесс, — самый верный способ его испортить.
    /// </remarks>
    private void Trim()
    {
        bool held = false;

        try
        {
            try
            {
                held = _mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            if (!File.Exists(_path) || new FileInfo(_path).Length <= SizeLimit)
                return;

            // Хвост полезнее головы: важно, чем кончилось, а не с чего
            // начиналось месяц назад.
            var lines = File.ReadAllLines(_path, Encoding.UTF8);

            File.WriteAllLines(
                _path,
                lines.Skip(Math.Max(0, lines.Length - KeepLines)),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
        }
        finally
        {
            if (held)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _mutex.Dispose();

        base.Dispose(disposing);
    }
}
