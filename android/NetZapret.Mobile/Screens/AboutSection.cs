using Avalonia.Controls;
using Avalonia.Layout;
using NetZapret.Core;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «О программе»: версия и состав с лицензиями.
/// </summary>
/// <remarks>
/// Состав — свой, а не <see cref="About.Components"/> целиком: в программе
/// для Windows едут WinDivert, Wintun и родной winws2, которых в телефоне нет,
/// а ByeDPI и Avalonia — наоборот, только здесь. sing-box — GPL v3: вместе
/// с ним приложение обязано называть лицензию и давать ссылку на исходники
/// (docs/android.md, «Лицензия»).
/// </remarks>
internal sealed class AboutSection : Section
{
    private static readonly string[] FromWindows = ["sing-box extended", "Zapret 2", "Zapret GUI", "zapret-discord-youtube", "ZXing.Net"];

    public AboutSection(MainView shell) : base(shell)
    {
        string version;

        try
        {
            version = Host.Facts().AppVersion;
        }
        catch (Exception)
        {
            version = typeof(AboutSection).Assembly.GetName().Version?.ToString(3) ?? "?";
        }

        Body.Children.Add(Ui.Card(Ui.Stack(6,
            Ui.Text("NetZapret", "h1"),
            Ui.Text("Версия " + version, "muted"),
            Ui.Text("Обход блокировок: туннель sing-box, десинк ByeDPI (nfqws2 с root) и маршруты — что куда идёт. "
                + $"Автор — {About.Author}. Лицензия MIT.", "small"))));

        var components = Ui.Stack(12, Ui.Caption("В составе"));

        var list = About.Components
            .Where(c => FromWindows.Contains(c.Name))
            .Select(c => c.Name == "Zapret 2" ? c with { Role = "Десинк с root: nfqws2 и его библиотека lua" } : c)
            .Concat(
            [
                new Component("ByeDPI", "Десинк без root: отдельный процесс на телефоне — автор hufrea",
                    "MIT", "https://github.com/hufrea/byedpi"),
                new Component("Avalonia", "Интерфейс приложения", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
            ]);

        foreach (var component in list)
        {
            var source = Ui.Button(component.Source.Replace("https://", string.Empty), () => _ = Host.OpenUrlAsync(component.Source), "link");
            source.HorizontalAlignment = HorizontalAlignment.Left;
            source.Padding = new Avalonia.Thickness(0, 6);

            components.Children.Add(Ui.Stack(2,
                Ui.Row(8, Ui.Text(component.Name, "h3"), Ui.Chip(component.License)),
                Ui.Text(component.Role, "muted", "small"),
                source));
        }

        Body.Children.Add(Ui.Card(components));

        Body.Children.Add(Ui.Card(Ui.Text("sing-box распространяется по GPL v3: его исходники — по ссылке выше "
            + "(тег v1.14.3-extended-2.7.3). Чужие движки едут в приложении как есть и запускаются отдельно; "
            + "условия каждого — в docs/THIRD-PARTY.md репозитория.", "muted", "small")));
    }

    public override string Title => "О программе";
}
