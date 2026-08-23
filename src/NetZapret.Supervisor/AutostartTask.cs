using System.Diagnostics;
using System.Security;
using System.Text;

namespace NetZapret.Supervisor;

public sealed class AutostartOptions
{
    public string TaskName { get; init; } = AutostartTask.DefaultTaskName;

    /// <summary>Полный путь к netzapret.exe.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Аргументы запуска — обычно строка команды <c>start</c>.</summary>
    public required string Arguments { get; init; }

    /// <summary>
    /// Рабочий каталог. Важен: пути конфигов в аргументах относительные,
    /// а задача иначе стартует в system32.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Учётная запись, под которой запускать.</summary>
    public required string UserId { get; init; }
}

/// <summary>
/// Автозапуск через планировщик заданий Windows.
/// </summary>
/// <remarks>
/// <para>
/// Планировщик, а не служба Windows. Нам нужны права администратора для TUN
/// и WinDivert; задача с наивысшими привилегиями поднимается при входе
/// и не показывает запрос UAC. Служба дала бы старт до входа в систему,
/// но потребовала бы переписать консольное приложение под сервисную модель
/// вместе с логами и остановкой — цена несопоставима с выигрышем.
/// </para>
/// <para>
/// Установка и удаление меняют состояние системы и требуют прав
/// администратора, поэтому выполняются только по явной команде пользователя.
/// </para>
/// </remarks>
public static class AutostartTask
{
    public const string DefaultTaskName = "NetZapret";

    private static readonly Encoding? OemEncoding = ResolveOemEncoding();

    /// <summary>
    /// Кодировка ответов планировщика.
    /// </summary>
    /// <remarks>
    /// schtasks отвечает в кодировке OEM. В .NET Core устаревших кодовых
    /// страниц нет без явной регистрации поставщика, а без них обращение
    /// падает с NotSupportedException. Если регистрация почему-то не удалась,
    /// возвращается <c>null</c> — тогда используется кодировка по умолчанию,
    /// сообщения будут нечитаемы, но код возврата останется верным.
    /// </remarks>
    private static Encoding? ResolveOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(Console.OutputEncoding.CodePage == 65001 ? 866 : Console.OutputEncoding.CodePage);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Строит XML описания задачи.
    /// </summary>
    /// <remarks>
    /// Через XML, а не через ключи <c>schtasks /create</c>: только так задаются
    /// неограниченное время выполнения и работа от батареи. По умолчанию
    /// планировщик убивает задачу через трое суток и не запускает её на
    /// ноутбуке без питания — для постоянно работающего десинка и то и другое
    /// неприемлемо.
    /// </remarks>
    public static string BuildXml(AutostartOptions options)
    {
        var command = SecurityElement.Escape(options.ExecutablePath);
        var arguments = SecurityElement.Escape(options.Arguments);
        var workingDirectory = SecurityElement.Escape(options.WorkingDirectory);
        var userId = SecurityElement.Escape(options.UserId);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>NetZapret: десинк и VPN под общим присмотром</Description>
                <URI>\{SecurityElement.Escape(options.TaskName)}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userId}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{userId}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{arguments}</Arguments>
                  <WorkingDirectory>{workingDirectory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Регистрирует задачу. Требует прав администратора.</summary>
    public static (bool Ok, string Output) Install(AutostartOptions options)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"netzapret-task-{Guid.NewGuid():N}.xml");

        try
        {
            // Планировщик принимает описание только в UTF-16.
            File.WriteAllText(xmlPath, BuildXml(options), new UnicodeEncoding(false, true));

            return RunSchtasks("/create", "/tn", options.TaskName, "/xml", xmlPath, "/f");
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
                // Временный файл; неудача удаления ни на что не влияет.
            }
        }
    }

    public static (bool Ok, string Output) Remove(string taskName) =>
        RunSchtasks("/delete", "/tn", taskName, "/f");

    public static bool IsInstalled(string taskName) =>
        RunSchtasks("/query", "/tn", taskName).Ok;

    /// <summary>Запускает уже установленную задачу немедленно.</summary>
    public static (bool Ok, string Output) RunNow(string taskName) =>
        RunSchtasks("/run", "/tn", taskName);

    private static (bool Ok, string Output) RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
                return (false, "не удалось запустить schtasks.exe");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }
}
