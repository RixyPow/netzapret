using System.Windows;

namespace NetZapret.Gui.Views;

/// <summary>
/// Журналы движков отдельным окном — открывается из «Диагностики» и с «Главной».
/// </summary>
/// <remarks>
/// Одно на программу: второе нажатие поднимает уже открытое, а не плодит
/// копии, читающие те же файлы. Без владельца — его можно отодвинуть
/// за главное окно и держать рядом, пока разбираешься в разделе.
/// </remarks>
public partial class LogWindow : Window
{
    private static LogWindow? _open;

    public LogWindow()
    {
        InitializeComponent();

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this))
                _open = null;
        };
    }

    /// <summary>Показывает журнал: открытый — поднимает, иначе открывает новый.</summary>
    public static void Open()
    {
        if (_open is { } shown)
        {
            if (shown.WindowState == WindowState.Minimized)
                shown.WindowState = WindowState.Normal;

            shown.Activate();
            return;
        }

        _open = new LogWindow();
        _open.Show();
    }
}
