using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NetZapret.Gui.Views;

namespace NetZapret.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        VersionLabel.Text = "версия " + Version();
        SourceInitialized += (_, _) => DarkenTitleBar();
    }

    /// <summary>
    /// Показывает выбранный раздел.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Каждый раз новый объект, а не сохранённый. Разделы читают состояние
    /// системы — движки, подписку, hosts, — и оно меняется, пока окно открыто.
    /// Возвращать сохранённый вид значило бы показывать снимок прошлого
    /// захода, не сказав об этом.
    /// </para>
    /// <para>
    /// Цена известна: замер серверов, начатый в одном разделе, прервётся при
    /// уходе в другой. Это честнее, чем оставлять его гоняться в невидимом
    /// разделе, тратя трафик подписки.
    /// </para>
    /// </remarks>
    private void OnSection(object sender, RoutedEventArgs e)
    {
        // Отрабатывает и при разборе разметки, когда Section ещё не создан:
        // IsChecked="True" у первого пункта поднимает событие раньше времени.
        if (Section is null || sender is not RadioButton { Tag: string name })
            return;

        Section.Content = name switch
        {
            "servers" => new ServersView(),
            _ => new StatusView(),
        };
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "—";

    /// <summary>Идентификатор атрибута тёмного оформления рамки.</summary>
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    /// <summary>
    /// Просит систему нарисовать заголовок окна тёмным.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Само окно тёмное, а рамку и заголовок рисует Windows, и по умолчанию
    /// они светлые. Белая полоса над тёмным содержимым — первое, что видно
    /// при запуске, и выглядит она поломкой, а не задумкой.
    /// </para>
    /// <para>
    /// Отказ не проверяется намеренно: на сборках Windows старше 2020 года
    /// атрибута нет, вызов вернёт ошибку, и заголовок останется светлым.
    /// Это некрасиво, но работать не мешает, а падать из-за оформления
    /// не стоит ничего.
    /// </para>
    /// </remarks>
    private void DarkenTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;

            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception)
        {
            // Оформление не стоит того, чтобы из-за него не открылось окно.
        }
    }
}
