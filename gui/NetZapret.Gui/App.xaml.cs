using System.IO;
using System.Windows;

namespace NetZapret.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Тот же поиск, что делает консоль: все пути в настройках, правилах
        // и списках заданы относительно корня установки, и без перехода туда
        // окно читало бы конфиг из System32 — именно там оказывается рабочий
        // каталог у программы, запущенной с повышением прав.
        MoveToInstallDirectory();
    }

    private static void MoveToInstallDirectory()
    {
        if (Directory.Exists("config"))
            return;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "config")))
            {
                Directory.SetCurrentDirectory(directory.FullName);
                return;
            }

            directory = directory.Parent;
        }

        // Не нашли — оставляем как есть. Разделы скажут об этом сами, каждый
        // про своё: так понятнее, чем одно окно с ошибкой при запуске.
    }
}
