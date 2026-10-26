using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Mobile.Shell;
using NetZapret.Proxy;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// DNS: резолвер движка, путь его запросов, пины.
/// </summary>
/// <remarks>
/// <para>
/// Резолверы — те же, что у вкладки DNS Windows (<see cref="DnsSurvey.All"/>,
/// годные туннелю — <see cref="DnsProvider.Choosable"/>): движок спрашивает их
/// по DoH адресом. Выбор руками выключает автовыбор — иначе следующий обзор
/// молча отменил бы решение человека (AppSettings.DnsAuto).
/// </para>
/// <para>
/// Пины на телефоне — не системный hosts, а файл <c>config/hosts</c> в папке
/// приложения: его читает DNS-сервер движка (android/DESIGN.md, «Пины»).
/// </para>
/// </remarks>
internal sealed class DnsSection : Section
{
    /// <summary>Файл пинов телефона — относительно папки данных.</summary>
    public static string PinsPath => Path.Combine("config", "hosts");

    private readonly StackPanel _resolvers = new() { Spacing = 6 };
    private readonly StackPanel _path = new() { Spacing = 10 };
    private readonly StackPanel _pins = new() { Spacing = 4 };
    private readonly TextBlock _survey = Ui.Text("", "muted", "small");
    private readonly Dictionary<string, DnsSurveyRow> _measured = new(StringComparer.Ordinal);
    private CancellationTokenSource? _surveying;
    private readonly Button _surveyButton;

    public DnsSection(MainView shell) : base(shell)
    {
        _surveyButton = Ui.Button("Проверить резолверы", () => _ = SurveyAsync());
        _surveyButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        Body.Children.Add(Ui.Card(Ui.Stack(10,
            Ui.Caption("Резолвер движка"),
            Ui.Text("Кого движок спрашивает об именах — по DoH, шифрованно: открытый DNS часть операторов подменяет.", "muted", "small"),
            _resolvers,
            _surveyButton,
            _survey)));

        Body.Children.Add(Ui.Card(_path));
        Body.Children.Add(Ui.Card(_pins));
        Body.Children.Add(PhoneCard());
    }

    public override string Title => "DNS";

    protected override void Shown()
    {
        ShowResolvers();
        ShowPath();
        ShowPins();
    }

    protected override void Hidden() => _surveying?.Cancel();

    private void ShowResolvers()
    {
        _resolvers.Children.Clear();
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        foreach (var provider in DnsSurvey.All.Where(p => p.Choosable || p.Own))
        {
            bool current = provider.TlsAddress == settings.DnsServer;
            var hint = provider.Note ?? string.Empty;

            if (_measured.TryGetValue(provider.Name, out var row))
            {
                hint = (row.DohMs is { } ms ? $"DoH {ms:0} мс" : "DoH не ответил: " + row.DohFailure)
                    + (row.Spoofed > 0 ? " · подменяет ответы" : string.Empty)
                    + (hint.Length > 0 ? " · " + hint : string.Empty);
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            grid.Children.Add(Ui.Stack(1, Ui.Text(provider.Name, "h3"), Ui.Text(hint, "muted", "small")));

            var address = Ui.Text(provider.TlsAddress, "faint", "small");
            address.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(address, 1);
            grid.Children.Add(address);

            var option = new Button { Content = grid, Classes = { "option" } };

            if (current)
                option.Classes.Add("selected");

            option.Click += (_, _) =>
            {
                if (provider.TlsAddress is not { } chosen)
                    return;

                var fresh = AppSettings.Load(AppSettings.DefaultPath);
                (fresh with { DnsServer = chosen, DnsAuto = false }).Save(AppSettings.DefaultPath);
                Shell.OfferRestart($"Резолвер: {provider.Name}");
                ShowResolvers();
            };

            _resolvers.Children.Add(option);
        }
    }

    private void ShowPath()
    {
        _path.Children.Clear();
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        DnsRoute[] routes = [DnsRoute.Auto, DnsRoute.Direct, DnsRoute.Tunnel];

        var explain = Ui.Text(DnsRoutes.Explain(settings), "muted", "small");

        _path.Children.Add(Ui.Caption("Путь запросов"));
        _path.Children.Add(Ui.Segmented(routes.Select(r => Capitalize(DnsRoutes.Word(r))).ToList(), Array.IndexOf(routes, settings.DnsVia), index =>
        {
            var fresh = AppSettings.Load(AppSettings.DefaultPath) with { DnsVia = routes[index] };
            fresh.Save(AppSettings.DefaultPath);
            explain.Text = DnsRoutes.Explain(fresh);
            Shell.OfferRestart("DNS: " + DnsRoutes.Word(routes[index]));
        }));
        _path.Children.Add(explain);
    }

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private void ShowPins()
    {
        _pins.Children.Clear();
        _pins.Children.Add(Ui.Caption("Пины"));

        Load(() => HostsEditor.PinsAll(PinsPath), pins =>
        {
            if (pins.Count == 0)
            {
                _pins.Children.Add(Ui.Text("Пинов нет. Пин прибивает имя к адресу посредника, когда сайт отказывает "
                    + "по стране. Подбор пина на телефоне — следующим шагом.", "muted", "small"));
                return;
            }

            foreach (var (name, addresses) in pins.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                grid.Children.Add(Ui.Stack(1, Ui.Text(name), Ui.Text(string.Join(", ", addresses), "muted", "small")));

                var remove = Ui.Button("Снять", () => Unpin(name), "link");
                Grid.SetColumn(remove, 1);
                grid.Children.Add(remove);
                _pins.Children.Add(grid);
            }
        }, _ => _pins.Children.Add(Ui.Text("Файл пинов не читается.", "warn")));
    }

    private void Unpin(string name)
    {
        try
        {
            HostsEditor.Unpin([name], PinsPath);
            Shell.OfferRestart($"Пин снят: {name}");
        }
        catch (Exception ex)
        {
            Shell.Say("Не снялось: " + ex.GetBaseException().Message);
        }

        ShowPins();
    }

    private async Task SurveyAsync()
    {
        if (_surveying is not null)
        {
            _surveying.Cancel();
            return;
        }

        _surveying = CancellationTokenSource.CreateLinkedTokenSource(WhileVisible);
        var token = _surveying.Token;
        _surveyButton.Content = new TextBlock { Text = "Остановить" };
        _survey.Text = "Проверяю резолверы: DoH, DoT и открытый DNS, по нескольку разом…";

        var progress = new Progress<DnsSurveyRow>(row =>
        {
            _measured[row.Provider.Name] = row;
            ShowResolvers();
        });

        try
        {
            var rows = await Task.Run(() => DnsSurvey.SurveyAllAsync(null, progress, token), token);
            var fastest = DnsSurvey.Fastest(rows);
            _survey.Text = fastest is null ? "Ни один не ответил по DoH." : $"Быстрейший по DoH — {fastest.Name}.";
        }
        catch (OperationCanceledException)
        {
            _survey.Text = "Проверка остановлена.";
        }
        catch (Exception ex)
        {
            _survey.Text = "Проверка не прошла: " + ex.GetBaseException().Message;
        }
        finally
        {
            _surveying.Dispose();
            _surveying = null;
            _surveyButton.Content = new TextBlock { Text = "Проверить резолверы" };
        }
    }

    private Control PhoneCard()
    {
        PlatformFacts? facts = null;

        try
        {
            facts = Host.Facts();
        }
        catch (Exception)
        {
            // Хозяин не ответил — карточка скажет, что не знает.
        }

        string text = facts?.PrivateDns switch
        {
            null when facts is null => "Не удалось узнать настройки телефона.",
            null => "Частный DNS телефона выключен — имена спрашивает движок, как и задумано.",
            "auto" => "Частный DNS телефона — «Автоматически». Это не мешает: телефон спрашивает DNS, который ему дал VPN.",
            var name => $"Частный DNS телефона — «{name}». Имена идут мимо движка, по TLS прямо к этому серверу: маршруты "
                + "по именам и пины не действуют. Поставьте «Автоматически» в настройках Android (Сеть → Частный DNS).",
        };

        var block = Ui.Text(text, facts?.PrivateDns is { } p && p != "auto" ? "warn" : "muted");
        return Ui.Card(Ui.Stack(8, Ui.Caption("Телефон"), block));
    }
}
