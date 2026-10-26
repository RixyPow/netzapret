using Avalonia.Controls;
using Avalonia.Threading;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// Выбор приложения для маршрута — по имени пакета.
/// </summary>
/// <remarks>
/// <para>
/// Маршрут приложения — обычное правило с <c>MatchKind.Process</c>, только
/// в значении имя пакета, а не exe (android/DESIGN.md §5): на Android
/// sing-box узнаёт приложение по пакету (<c>package_name</c>). На Windows
/// такие правила снимает <c>ProgramRulesOff</c> — на телефоне его не зовут.
/// </para>
/// <para>
/// Системные скрыты по умолчанию: их десятки, и путь им менять незачем —
/// а снять с VPN «Сервисы Google Play» случайно легко.
/// </para>
/// </remarks>
internal sealed class AppsSection : Section
{
    private readonly Action<InstalledApp, PathPick> _chosen;
    private readonly TextBox _search = new() { PlaceholderText = "Найти приложение" };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private IReadOnlyList<InstalledApp>? _apps;
    private bool _system;
    private DispatcherTimer? _typing;

    public AppsSection(MainView shell, Action<InstalledApp, PathPick> chosen) : base(shell)
    {
        _chosen = chosen;

        _search.TextChanged += (_, _) =>
        {
            _typing?.Stop();
            _typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _typing.Tick += (_, _) =>
            {
                _typing?.Stop();
                ShowList();
            };
            _typing.Start();
        };

        Body.Children.Add(_search);
        Body.Children.Add(Ui.Card(Ui.Switch("Системные приложения", "Встроенные в Android — обычно им путь не меняют", false, on =>
        {
            _system = on;
            ShowList();
        })));
        Body.Children.Add(Ui.Card(_list));
    }

    public override string Title => "Добавить приложение";

    protected override void Shown()
    {
        if (_apps is not null)
            return;

        _list.Children.Add(Placeholder("Читаю список приложений…"));

        Load(() => Host.InstalledApps().OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList(), apps =>
        {
            _apps = apps;
            ShowList();
        });
    }

    private void ShowList()
    {
        if (_apps is null)
            return;

        var query = _search.Text?.Trim() ?? string.Empty;
        _list.Children.Clear();

        var shown = _apps
            .Where(a => _system || !a.System)
            .Where(a => query.Length == 0
                || a.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(200)
            .ToList();

        if (shown.Count == 0)
            _list.Children.Add(Ui.Text("Ничего не нашлось.", "muted"));

        foreach (var app in shown)
        {
            var row = new Button
            {
                Content = Ui.Stack(1, Ui.Text(app.Label), Ui.Text(app.Package, "muted", "small")),
                Classes = { "row" },
                MinHeight = 52,
            };

            row.Click += (_, _) => PathSheet.Show(Shell, app.Label, PathPick.Default, null, false, false, pick =>
            {
                _chosen(app, pick);
                Shell.Back();
            });

            _list.Children.Add(row);
        }
    }
}
