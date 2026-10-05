using System.Windows;
using NetZapret.Core;

namespace NetZapret.Gui.Views;

/// <summary>
/// Настройки маршрутов — отдельным окном, как «Настройки туннеля» у вкладки VPN.
/// </summary>
/// <remarks>
/// Оба выключателя движкам безразличны: первый решает, предлагать ли
/// перезапуск Discord, второй читает сторож голоса (DiscordVoiceWatch)
/// на каждом круге. Поэтому перезапуск движков здесь не предлагается.
/// </remarks>
public partial class RoutesSettingsWindow : Window
{
    public RoutesSettingsWindow()
    {
        InitializeComponent();

        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 40);

        Show(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void Show(AppSettings settings)
    {
        Restart.IsChecked = settings.OfferDiscordRestart;
        RestartWord.Text = settings.OfferDiscordRestart ? "вкл." : "выкл.";

        Learn.IsChecked = settings.LearnDiscordVoice;
        LearnWord.Text = settings.LearnDiscordVoice ? "вкл." : "выкл.";
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        bool on = Restart.IsChecked == true;

        Save(s => s with { OfferDiscordRestart = on },
            on ? "После смены маршрута Discord программа предложит перезапустить его."
               : "Перезапуск Discord больше не предлагается.");
    }

    private void OnLearn(object sender, RoutedEventArgs e)
    {
        bool on = Learn.IsChecked == true;

        Save(s => s with { LearnDiscordVoice = on },
            on ? "Адреса голоса Discord дописываются, пока голос «через VPN»."
               : "Список голоса Discord больше не пополняется сам.");
    }

    /// <summary>Перечитать, изменить, записать — окно могло пережить чужую правку настроек.</summary>
    private void Save(Func<AppSettings, AppSettings> change, string said)
    {
        try
        {
            var next = change(AppSettings.Load(AppSettings.DefaultPath));

            next.Save(AppSettings.DefaultPath);
            Show(next);
            Status.Text = said;
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
            Show(AppSettings.Load(AppSettings.DefaultPath));
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
