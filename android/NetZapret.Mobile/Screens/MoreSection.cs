using NetZapret.Core;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Ещё»: всё, чему не нашлось места в нижнем меню.
/// </summary>
/// <remarks>
/// На Windows это отдельные разделы боковой колонки (DNS, «Замер скорости»,
/// «Диагностика») и раздел «Ещё»; на телефоне пять пунктов меню — предел,
/// и реже нужное собрано здесь (ui.md §3.8).
/// </remarks>
internal sealed class MoreSection : Section
{
    public MoreSection(MainView shell) : base(shell)
    {
        Body.Children.Add(Ui.Card(Ui.Stack(0,
            Ui.Caption("Разбор неисправностей"),
            Ui.NavRow("DNS", "Резолвер движка, его путь и пины", () => Shell.Open(new DnsSection(Shell)), Icons.Dns),
            Ui.NavRow("Замер скорости", "Скачивание, отдача и задержка", () => Shell.Open(new SpeedSection(Shell)), Icons.Speed),
            Ui.NavRow("Диагностика", "Что на этом телефоне мешает VPN", () => Shell.Open(new DoctorSection(Shell)), Icons.Doctor),
            Ui.NavRow("Журнал", "Последние строки движка", () => Shell.Open(new LogSection(Shell)), Icons.Log))));

        Body.Children.Add(Ui.Card(Ui.Stack(0,
            Ui.Caption("Программа"),
            Ui.NavRow("Настройки", "Десинк, туннель", () => Shell.Open(new SettingsSection(Shell)), Icons.Settings),
            Ui.NavRow("Мастер первого запуска", "Знакомство и выбор режима — ещё раз", Shell.ShowOnboarding, Icons.Wand),
            Ui.NavRow("О программе", "Версия, состав и лицензии", () => Shell.Open(new AboutSection(Shell)), Icons.Info))));

        Body.Children.Add(Ui.Card(Ui.Stack(0,
            Ui.Caption("Ссылки"),
            Link("Репозиторий", About.Repository),
            Link("Сообщить о проблеме", About.Issues),
            Link("Обсуждения", About.Discussions),
            Link("Телеграм-канал", About.Telegram),
            Link("Поддержать", About.Support))));
    }

    public override string Title => "Ещё";

    private Avalonia.Controls.Button Link(string title, string url) =>
        Ui.NavRow(title, url.Replace("https://", string.Empty), () => _ = Host.OpenUrlAsync(url), Icons.Link);
}
