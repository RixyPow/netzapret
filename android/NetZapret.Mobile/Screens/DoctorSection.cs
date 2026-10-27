using Avalonia.Controls;
using Avalonia.Layout;
using NetZapret.Core;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Диагностика» телефона: что на этом устройстве мешает VPN и обходу.
/// </summary>
/// <remarks>
/// <para>
/// Проверки Windows (<c>Doctor</c> в NetZapret.Supervisor) — про адаптеры,
/// службы, Defender и WinDivert; на телефоне их нет, а библиотека надзора
/// тянет ETW и сюда не подключается. Здесь — то, что ломает VPN на Android:
/// частный DNS, сон приложения, второй VPN, root для nfqws2 (docs/android.md §6).
/// </para>
/// <para>
/// Отчёт для разбора (<c>SupportReport</c>) — следующим шагом, когда сборка
/// отчёта переедет из надзора в библиотеку без Windows.
/// </para>
/// </remarks>
internal sealed class DoctorSection : Section
{
    private readonly StackPanel _checks = new() { Spacing = 10 };

    public DoctorSection(MainView shell) : base(shell)
    {
        var again = Ui.Button("Проверить снова", Render);
        again.HorizontalAlignment = HorizontalAlignment.Stretch;

        Body.Children.Add(_checks);
        Body.Children.Add(again);
    }

    public override string Title => "Диагностика";

    protected override void Shown() => Render();

    private enum Level { Good, Warn, Bad, Info }

    private void Render()
    {
        _checks.Children.Clear();
        PlatformFacts? facts = null;
        string? failure = null;

        try
        {
            facts = Host.Facts();
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
        }

        var status = Host.Status;
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var mobile = MobileSettings.Load();
        var rows = new List<(Level, string, string)>();

        if (facts is null)
        {
            rows.Add((Level.Bad, "Сведения о телефоне", "Хозяин не ответил: " + failure));
        }
        else
        {
            rows.Add(facts.PrivateDns switch
            {
                null => (Level.Good, "Частный DNS", "Выключен — имена спрашивает движок."),
                "auto" => (Level.Good, "Частный DNS", "«Автоматически» — телефон спрашивает DNS, который дал VPN."),
                var name => (Level.Bad, "Частный DNS", $"«{name}»: имена идут мимо движка, маршруты по именам и пины не действуют. "
                    + "Настройки Android → Сеть → Частный DNS → «Автоматически»."),
            });

            rows.Add(facts.BatteryOptimized
                ? (Level.Warn, "Оптимизация батареи", "Включена: Android вправе усыпить приложение, и VPN упадёт в фоне. "
                    + "Снимите её для NetZapret в настройках приложения.")
                : (Level.Good, "Оптимизация батареи", "Снята — VPN не усыпят."));

            rows.Add(facts.OtherVpnActive
                ? (Level.Warn, "Другой VPN", "Работает другой VPN-клиент. Android держит один VPN: кто запустится вторым, отключит первого.")
                : (Level.Good, "Другой VPN", "Других VPN не видно."));

            rows.Add(facts.AlwaysOn
                ? (Level.Good, "Постоянный VPN", "Включён для NetZapret — Android поднимет его сам после перезагрузки.")
                : (Level.Info, "Постоянный VPN", "Выключен. Чтобы VPN поднимался сам после перезагрузки — настройки Android → VPN → NetZapret."));

            // Root хозяин спрашивает только при запуске и только с включённым
            // nfqws2, так что «нет» в сведениях — и отказ, и «не спрашивали».
            // Прежде здесь стояло «Нет» на любом телефоне без включённого nfqws2,
            // в том числе с Magisk (разбор 27.10).
            var withoutRoot = mobile.Desync == DesyncEngine.ByeDpi ? "ByeDPI" : "нарезка приветствия в sing-box";

            rows.Add(facts.Root
                ? (Level.Info, "Root", mobile.UseRoot ? "Есть, nfqws2 включён в настройках." : $"Есть. nfqws2 включается в «Настройках»; без него десинк — {withoutRoot}.")
                : mobile.UseRoot
                    ? (Level.Info, "Root", "Не подтверждён. Его спрашивают при запуске движков; менеджер root отказал или "
                        + $"root нет — десинк идёт без него: {withoutRoot}. Отказ меняют в самом менеджере (Magisk) и перезапускают приложение.")
                    : (Level.Info, "Root", $"Не проверялся: он нужен только nfqws2, а тот выключен — десинк идёт без него: {withoutRoot}. "
                        + "Включить nfqws2 — в «Настройках», root спросят при запуске."));

            rows.Add((Level.Info, "Устройство", $"Android API {facts.Sdk}, {facts.Abi}, NetZapret {facts.AppVersion}"));
        }

        rows.Add(status.State switch
        {
            "running" => (Level.Good, "Движки", "Работают" + (status.DesyncRestarts > 0 ? $"; десинк перезапускался: {status.DesyncRestarts}." : ".")),
            "failed" => (Level.Bad, "Движки", "Не работают: " + (status.Error ?? "причина не названа")),
            _ => (Level.Info, "Движки", "Остановлены."),
        });

        rows.Add(settings.HasTunnelExit
            ? (Level.Good, "Выход VPN", settings.WarpEnabled ? "WARP." : "Есть подписка или ключи.")
            : (Level.Warn, "Выход VPN", "Нет ни подписки, ни WARP: работает только десинк."));

        int bad = rows.Count(r => r.Item1 == Level.Bad);
        int warn = rows.Count(r => r.Item1 == Level.Warn);

        _checks.Children.Add(Ui.Card(Ui.Stack(4,
            Ui.Text(bad > 0 ? "Есть поломки" : warn > 0 ? "Работает с оговорками" : "Всё в порядке", "h2"),
            Ui.Text($"Поломок: {bad}, предупреждений: {warn}.", "muted"))));

        foreach (var (level, title, text) in rows)
        {
            var colour = level switch
            {
                Level.Good => "Accent",
                Level.Warn => "Warn",
                Level.Bad => "Danger",
                _ => "Faint",
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
            var dot = Ui.Dot(Ui.Brush(colour), 12);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Avalonia.Thickness(0, 5, 0, 0);
            grid.Children.Add(dot);

            var texts = Ui.Stack(2, Ui.Text(title, "h3"), Ui.Text(text, "muted", "small"));
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            _checks.Children.Add(Ui.Card(grid));
        }
    }
}
