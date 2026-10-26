using Avalonia.Controls;
using Avalonia.Layout;
using NetZapret.Core;
using NetZapret.Mobile.Shell;
using NetZapret.Zapret;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Настройки»: чем делать десинк на телефоне и как вести себя туннелю.
/// </summary>
/// <remarks>
/// <para>
/// Десинк без root — ByeDPI отдельным процессом (android/DESIGN.md), его
/// стратегия — по выбранному пресету (те же секции, что у winws2 на Windows,
/// переведённые <c>ByeDpiCommandLine</c>), встроенная по умолчанию или своя
/// строка. С root — nfqws2 с тем же пресетом, но только по выбору человека.
/// </para>
/// <para>
/// Пишется в <c>config/mobile.json</c> (<see cref="MobileSettings"/>) — кроме
/// пресета и выключателей туннеля: они общие с Windows и живут в
/// <c>config/netzapret.json</c>.
/// </para>
/// </remarks>
internal sealed class SettingsSection : Section
{
    private readonly StackPanel _desync = new() { Spacing = 12 };

    public SettingsSection(MainView shell) : base(shell)
    {
        Body.Children.Add(Ui.Card(_desync));
        Body.Children.Add(TunnelCard());

        Body.Children.Add(Ui.Card(Ui.Stack(0,
            Ui.Caption("DNS"),
            Ui.NavRow("Резолвер и путь DNS", null, () => Shell.Open(new DnsSection(Shell)), Icons.Dns))));

        ShowDesync();
    }

    public override string Title => "Настройки";

    private void ShowDesync()
    {
        _desync.Children.Clear();
        var mobile = MobileSettings.Load();
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        bool root = SafeRoot();

        _desync.Children.Add(Ui.Caption("Десинк"));

        if (root)
        {
            _desync.Children.Add(Ui.Switch(
                "nfqws2 с root",
                "Тот же движок, что winws2 на Windows, с теми же пресетами и списками. Без root — ByeDPI.",
                mobile.UseRoot,
                on => Save(MobileSettings.Load() with { UseRoot = on }, on ? "Десинк: nfqws2 (root)" : "Десинк: без root")));
        }

        bool rootOn = root && mobile.UseRoot;

        if (!rootOn)
        {
            _desync.Children.Add(Ui.Segmented(["ByeDPI", "Фрагментация"], mobile.Desync == DesyncEngine.ByeDpi ? 0 : 1, index =>
                Save(MobileSettings.Load() with { Desync = index == 0 ? DesyncEngine.ByeDpi : DesyncEngine.Fragment },
                    index == 0 ? "Десинк: ByeDPI" : "Десинк: фрагментация sing-box")));

            _desync.Children.Add(Ui.Text(mobile.Desync == DesyncEngine.ByeDpi
                ? "ByeDPI — отдельный процесс без root: имена с путём «Десинк» sing-box отдаёт ему, и он ломает "
                  + "рукопожатие так, что фильтр оператора не узнаёт имя. Адрес остаётся домашним."
                : "Без ByeDPI: sing-box сам режет приветствие TLS на части. Проще и легче, но против части "
                  + "фильтров слабее.", "muted", "small"));
        }

        if (!rootOn && mobile.Desync == DesyncEngine.ByeDpi)
        {
            _desync.Children.Add(Ui.Text("Стратегия ByeDPI", "h3"));
            _desync.Children.Add(Ui.Segmented(["По пресету", "По умолчанию", "Своя"], (int)mobile.Strategy, index =>
                Save(MobileSettings.Load() with { Strategy = (DesyncStrategy)index }, "Стратегия ByeDPI изменена")));

            if (mobile.Strategy == DesyncStrategy.Custom)
            {
                var box = new TextBox
                {
                    Text = mobile.CustomArguments ?? string.Empty,
                    PlaceholderText = "Ключи ByeDPI, например: -o1 -d1 -An",
                    AcceptsReturn = true,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MinHeight = 96,
                    Classes = { "mono" },
                };

                var keep = Ui.Button("Сохранить строку", () =>
                    Save(MobileSettings.Load() with { CustomArguments = box.Text?.Trim() }, "Своя строка ByeDPI сохранена"), "primary");
                keep.HorizontalAlignment = HorizontalAlignment.Stretch;

                _desync.Children.Add(Ui.Text("Своя строка заменяет пресет целиком. Вход SOCKS, порт и пароль "
                    + "программа ставит сама — их в строке не нужно.", "muted", "small"));
                _desync.Children.Add(box);
                _desync.Children.Add(keep);
            }
        }

        if (rootOn || mobile.Desync == DesyncEngine.ByeDpi && mobile.Strategy == DesyncStrategy.Preset)
        {
            _desync.Children.Add(Ui.Text("Пресет", "h3"));
            _desync.Children.Add(Ui.Text("Те же пресеты, что у Windows: секции переводятся в стратегию "
                + (rootOn ? "nfqws2." : "ByeDPI. Что без root не переводится, пропускается."), "muted", "small"));

            // Пресетов полтора десятка: списком в карточке выбранный уезжал за экран.
            // Здесь — выбранный строкой, весь список — листом.
            _desync.Children.Add(Ui.NavRow(settings.PresetName ?? "не выбран", "Сменить пресет", ChoosePreset, Icons.Settings));
        }
    }

    private void ChoosePreset()
    {
        var current = AppSettings.Load(AppSettings.DefaultPath).PresetName;
        Sheet? sheet = null;
        var presets = Ui.Stack(6);

        foreach (var file in ZapretPaths.PresetFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var option = new Button { Content = Ui.Text(name), Classes = { "option" }, MinHeight = 48 };

            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                option.Classes.Add("selected");

            option.Click += (_, _) =>
            {
                sheet?.Close();
                var fresh = AppSettings.Load(AppSettings.DefaultPath);
                (fresh with { PresetName = name }).Save(AppSettings.DefaultPath);
                Shell.OfferRestart($"Пресет: {name}");
                ShowDesync();
            };

            presets.Children.Add(option);
        }

        if (presets.Children.Count == 0)
            presets.Children.Add(Ui.Text("Пресетов не нашлось в папке программы.", "warn"));

        sheet = Shell.ShowSheet("Пресет", presets);
    }

    private bool SafeRoot()
    {
        try
        {
            return Host.Facts().Root;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Save(MobileSettings settings, string what)
    {
        try
        {
            settings.Save();
            Shell.OfferRestart(what);
        }
        catch (Exception ex)
        {
            Shell.Say("Не записалось: " + ex.GetBaseException().Message);
        }

        ShowDesync();
    }

    private Control TunnelCard()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        void Write(Func<AppSettings, AppSettings> change, string what)
        {
            try
            {
                change(AppSettings.Load(AppSettings.DefaultPath)).Save(AppSettings.DefaultPath);
                Shell.OfferRestart(what);
            }
            catch (Exception ex)
            {
                Shell.Say("Не записалось: " + ex.GetBaseException().Message);
            }
        }

        return Ui.Card(Ui.Stack(12,
            Ui.Caption("Туннель"),
            Ui.Switch("Только зарубежные",
                "Автоподбор не берёт отечественные выходы: через них не работает то, ради чего туннель нужен",
                settings.ForeignExitsOnly,
                on => Write(s => s with { ForeignExitsOnly = on }, on ? "Только зарубежные" : "Все выходы")),
            Ui.Switch("Сначала стабильные",
                "Серверы, что отвечают через раз, автоподбор берёт последними",
                settings.StableFirst,
                on => Write(s => s with { StableFirst = on }, on ? "Сначала стабильные" : "Только по задержке")),
            Ui.Switch("Обход, если туннель лёг",
                "Ни один выход не отвечает — трафик идёт напрямую, пока туннель не оживёт",
                settings.BypassWhenTunnelDead,
                on => Write(s => s with { BypassWhenTunnelDead = on }, on ? "Обход при мёртвом туннеле" : "Без обхода"))));
    }
}
