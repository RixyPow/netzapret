using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Одна строка отчёта диагностики.</summary>
public sealed record DoctorLine(string Text, Brush Color);

/// <summary>Раздел отчёта.</summary>
public sealed record DoctorSection(string Title, IReadOnlyList<DoctorLine> Lines);

/// <summary>
/// Диагностика: всё, от чего зависит работа, разом.
/// </summary>
/// <remarks>
/// <para>
/// Повторяет команду <c>doctor</c> консоли. Ценность в порядке: проверки идут
/// от того, без чего не работает ничего, к тому, что ломает частности, —
/// и первая же красная строка обычно и есть ответ.
/// </para>
/// <para>
/// Здесь же вопрос «куда пойдёт это имя». Он задаётся ровно тогда, когда
/// сервис уходит не туда, то есть в ту же минуту, когда открывают этот
/// раздел, — и разносить их по разным местам значило бы заставлять человека
/// с одной бедой ходить по двум.
/// </para>
/// </remarks>
public partial class DoctorView : UserControl
{
    public DoctorView()
    {
        InitializeComponent();

        Loaded += (_, _) => Run();
    }

    private void OnRun(object sender, RoutedEventArgs e) => Run();

    private void Run()
    {
        RunButton.IsEnabled = false;
        Status.Text = "Смотрю…";

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var sections = new List<DoctorSection>
            {
                new("Права", Elevation()),
                new("Движки", Engines()),
                new("Десинк", Zapret(settings)),
                new("Правила", Rules(settings)),
                new("Конфиг туннеля", Proxy(settings)),
                new("Имена и hosts", Names(settings)),
                new("Супервизор", Supervisor()),
            };

            Sections.ItemsSource = sections;

            var lines = sections.SelectMany(s => s.Lines).ToList();
            int bad = lines.Count(l => Is(l, "Danger"));
            int warn = lines.Count(l => Is(l, "Warn"));

            Status.Text = bad == 0 && warn == 0
                ? $"Всё на месте: проверок {lines.Count}, ни одной жалобы."
                : $"Проверок {lines.Count}: поломок {bad}, оговорок {warn}. "
                  + "Красное чинить первым — остальное часто следствие.";
        }
        catch (Exception ex)
        {
            Status.Text = "Диагностика сорвалась: " + ex.GetBaseException().Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private bool Is(DoctorLine line, string key) =>
        ReferenceEquals(line.Color, FindResource(key));

    private DoctorLine Ok(string text) => new(text, (Brush)FindResource("Accent"));

    private DoctorLine Warn(string text) => new(text, (Brush)FindResource("Warn"));

    private DoctorLine Bad(string text) => new(text, (Brush)FindResource("Danger"));

    private IReadOnlyList<DoctorLine> Elevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        return
        [
            elevated
                ? Ok("Администратор: доступны TUN, winws2 и наблюдение.")
                : Bad("Обычные права. Окно должно запускаться от администратора — "
                    + "иначе не поставить драйвер перехвата и не поднять TUN."),
        ];
    }

    private IReadOnlyList<DoctorLine> Engines()
    {
        var lines = new List<DoctorLine>();
        var singBox = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

        if (File.Exists(singBox))
        {
            lines.Add(Ok("sing-box на месте."));

            // Без wintun.dll TUN не поднимется, а супервизор скажет об этом
            // только в журнал: со стороны это «работает, но интернета нет».
            var wintun = Path.Combine(Path.GetDirectoryName(singBox)!, "wintun.dll");

            lines.Add(File.Exists(wintun)
                ? Ok("wintun.dll рядом — TUN сможет подняться.")
                : Bad("wintun.dll рядом с sing-box нет: TUN не поднимется."));
        }
        else
        {
            lines.Add(Bad($"sing-box не найден: {singBox}. VPN недоступен, десинк работает."));
        }

        var console = Path.Combine(AppContext.BaseDirectory, "netzapret.exe");

        lines.Add(File.Exists(console)
            ? Ok("Консольная программа рядом — есть чем запускать движки.")
            : Bad($"netzapret.exe рядом с окном нет: {console}. Запуск и остановка не сработают."));

        return lines;
    }

    private IReadOnlyList<DoctorLine> Zapret(AppSettings settings)
    {
        var paths = ZapretPaths.Discover();

        if (paths is null)
            return [Warn("Установка Zapret не найдена — десинк недоступен.")];

        var lines = new List<DoctorLine> { Ok($"Установка: {paths.Root}") };

        lines.Add(File.Exists(paths.ExecutablePath)
            ? Ok("winws2.exe на месте.")
            : Bad($"winws2.exe не найден: {paths.ExecutablePath}. Возможно, антивирус увёз "
                + "его в карантин — WinDivert рядом с ним помечается как RiskTool."));

        try
        {
            int count = ZapretPaths.PresetFiles.Count;

            lines.Add(count > 0
                ? Ok($"Пресетов: {count}.")
                : Warn("Пресетов не найдено."));
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Пресеты не читаются: " + ex.GetBaseException().Message));
        }

        // Выбранный пресет мог исчезнуть: настройки переживают обновление,
        // а состав папки — нет. Без этой проверки супервизор поднимется
        // без десинка, сказав об этом только в журнал.
        if (settings.PresetName is { } chosen)
        {
            lines.Add(ZapretPaths.FindPreset(chosen) is not null
                ? Ok($"Выбран пресет: {chosen}.")
                : Bad($"Выбранного пресета «{chosen}» здесь нет — десинк не запустится."));
        }
        else
        {
            lines.Add(Warn("Пресет не выбран — десинк не запустится."));
        }

        return lines;
    }

    private IReadOnlyList<DoctorLine> Rules(AppSettings settings)
    {
        var lines = new List<DoctorLine>();

        try
        {
            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            var problems = RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

            int own = engine.RuleSet.Rules.Count(r => r.Source == RuleSource.User);

            lines.Add(Ok($"Режим {settings.DescribeMode()}, правил {engine.RuleSet.Rules.Count}"
                + (own > 0 ? $", из них ваших {own}." : ".")));

            // Ненайденный список не совпадает ни с чем, оставаясь на вид живым:
            // правило есть, а трафик мимо.
            lines.Add(problems.Count == 0
                ? Ok("Все списки на месте.")
                : Bad($"Списков не нашлось: {problems.Count}. Эти правила не действуют — "
                    + string.Join("; ", problems.Take(3))));

            var conflicts = RouteConflicts.Find(engine.RuleSet);

            lines.Add(conflicts.Count == 0
                ? Ok("Правила друг друга не перекрывают.")
                : Warn($"Правил, перекрытых другими: {conflicts.Count}. Побеждает то, "
                    + $"до которого очередь доходит раньше; например {conflicts[0].Loser.Value} "
                    + $"перекрыто {conflicts[0].Winner.Value}."));
        }
        catch (Exception ex)
        {
            lines.Add(Bad("Правила не читаются: " + ex.GetBaseException().Message));
        }

        return lines;
    }

    private IReadOnlyList<DoctorLine> Proxy(AppSettings settings)
    {
        var lines = new List<DoctorLine>();

        if (!settings.NeedsProxy)
        {
            lines.Add(Ok($"Режим «{settings.DescribeMode()}»: туннель не нужен."));
            return lines;
        }

        lines.Add(string.IsNullOrWhiteSpace(settings.SubscriptionUrl)
            ? Bad("Подписка не задана, а режим требует туннеля: серверов нет.")
            : Ok("Подписка задана."));

        var path = settings.ProxyConfigPath;

        lines.Add(File.Exists(path)
            ? Ok($"Конфиг собран: {path}")
            : Warn($"{path} не собран — соберётся при первом запуске движков."));

        lines.Add(settings.PreferredServer is { } server
            ? Ok($"Сервер выбран: {server}.")
            : Ok("Сервер подбирается по задержке."));

        return lines;
    }

    private IReadOnlyList<DoctorLine> Names(AppSettings settings)
    {
        var lines = new List<DoctorLine>
        {
            Ok($"Апстрим туннеля: {settings.DnsServer}"
                + (settings.DnsThroughTunnel ? ", запросы идут внутри туннеля." : ".")),
        };

        try
        {
            var found = SystemResolvers.Discover();

            lines.Add(found.Count > 0
                ? Ok($"Резолверы системы: {string.Join(", ", found)}")
                : Warn("Резолверы системы не нашлись."));
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Резолверы системы не читаются: " + ex.GetBaseException().Message));
        }

        try
        {
            if (HostsEditor.WhoReplaced() is { } who)
            {
                lines.Add(Bad($"Файл hosts переписан: {who}. Изменённый hosts он считает "
                    + "признаком заражения и вернул файл к своему умолчанию — вместе "
                    + "со всеми пинами."));
            }
            else
            {
                int pins = HostsEditor.Pins().Count;

                lines.Add(pins == 0
                    ? Ok("Прибитых имён нет.")
                    : Ok($"Прибито имён: {pins}."));
            }
        }
        catch (Exception ex)
        {
            lines.Add(Warn("Файл hosts не читается: " + ex.GetBaseException().Message));
        }

        var guards = SecuritySoftware.Running();

        if (guards.Count > 0)
        {
            lines.Add(Warn($"Работает {string.Join(", ", guards.Select(g => g.Name))}. "
                + "Его сетевой фильтр встаёт на тот же слой, что и наш, и обесценивает "
                + "десинк, ничего об этом не сообщая."));
        }

        return lines;
    }

    /// <summary>
    /// Движки от прошлого запуска, оставшиеся без присмотра.
    /// </summary>
    /// <remarks>
    /// Самая незаметная из неисправностей и потому первая, о которой стоит
    /// сказать. Осиротевший sing-box держит TUN-адаптер, осиротевший winws2 —
    /// WinDivert; новый движок поднимается процессом и не проходит проверку,
    /// а окно показывает «запущен, но не отвечает», не называя причины.
    /// Видно её было только в журнале супервизора.
    /// </remarks>
    private IReadOnlyList<DoctorLine> Orphans(SupervisorState? state)
    {
        try
        {
            var ours = state?.Services
                .Select(s => s.ProcessId)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .ToHashSet() ?? [];

            var strays = new[] { "sing-box", "winws2", "winws" }
                .SelectMany(System.Diagnostics.Process.GetProcessesByName)
                .Where(p => !ours.Contains(p.Id))
                .Select(p => $"{p.ProcessName} (PID {p.Id})")
                .ToList();

            return strays.Count == 0
                ? [Ok("Движков от прошлых запусков нет.")]
                : [Bad($"Движки без присмотра: {string.Join(", ", strays)}. Они держат "
                    + "TUN-адаптер и WinDivert, и новый движок не поднимется — он будет "
                    + "числиться запущенным и не отвечать. Остановите движки и запустите "
                    + "заново: запуск из окна убирает такие сам.")];
        }
        catch (Exception ex)
        {
            return [Warn("Процессы не перечисляются: " + ex.GetBaseException().Message)];
        }
    }

    private IReadOnlyList<DoctorLine> Supervisor()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool alive = state is not null && state.IsSupervisorAlive();

        var lines = new List<DoctorLine>(Orphans(alive ? state : null));

        if (!alive)
        {
            lines.Add(Warn("Движки не запущены — обход не работает, трафик идёт напрямую."));
            return lines;
        }

        foreach (var service in state!.Services)
        {
            lines.Add(service.Health switch
            {
                ServiceHealth.Healthy => Ok($"{service.Name}: работает."),

                // Самый коварный случай: процесс жив, а проверка не проходит.
                // Назвать его «работает» значит уверять в исправности молчащей
                // трубы.
                ServiceHealth.Degraded => Warn($"{service.Name}: запущен, но не отвечает."),
                ServiceHealth.Dead => Bad($"{service.Name}: процесс умер."),
                _ => Warn($"{service.Name}: остановлен."),
            });
        }

        return lines;
    }

    private void OnHostKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        HostHint.Visibility = Host.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (e.Key == System.Windows.Input.Key.Enter)
            Ask();
    }

    private void OnAsk(object sender, RoutedEventArgs e) => Ask();

    /// <summary>
    /// Прогоняет имя через настоящий движок правил.
    /// </summary>
    /// <remarks>
    /// Через движок, а не поиском правила по совпадению строк: иначе ответ
    /// разошёлся бы с действительным ровно в тех случаях, ради которых
    /// вопрос и задают, — при перекрытии правил.
    /// </remarks>
    private void Ask()
    {
        var host = Host.Text.Trim().Trim('/').ToLowerInvariant();

        if (host.Contains("://"))
            host = host.Split("://")[1];

        host = host.Split('/')[0].TrimStart('*', '.');

        HostHint.Visibility = Host.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (host.Length == 0 || !host.Contains('.'))
        {
            AnswerCard.Visibility = Visibility.Visible;
            AnswerMode.Text = "Это не похоже на имя сайта";
            AnswerMode.Foreground = (Brush)FindResource("Warn");
            AnswerWhy.Text = "Нужно что-то вроде web.whatsapp.com.";

            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

            var decision = engine.Evaluate(new ConnectionEvent
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = ProtocolKind.Tcp,

                // Адрес не подставляется намеренно: мы спрашиваем про имя,
                // адреса у него сейчас нет, и выдуманный попадает в чужие
                // правила. IPAddress.None — это 255.255.255.255, и он
                // однажды уже попал в список российских подсетей, отчего
                // все сервисы показывались идущими напрямую.
                RemoteAddress = null,
                RemotePort = 443,
                Hostname = host,
            });

            var (mode, key) = decision.Mode switch
            {
                RoutingMode.Direct => ("напрямую", "Muted"),
                RoutingMode.Desync => ("через десинк", "Warn"),
                _ => ("через VPN", "Accent"),
            };

            AnswerCard.Visibility = Visibility.Visible;
            AnswerMode.Text = $"{host} → {mode}";
            AnswerMode.Foreground = (Brush)FindResource(key);

            AnswerWhy.Text = decision.Rule is { } rule
                ? $"Сработало правило: {rule.Match.ToString().ToLowerInvariant()} «{rule.Value}»"
                  + (decision.Reason is null ? "." : $" — {decision.Reason}.")
                  + (rule.Source == RuleSource.User ? " Это ваше правило." : " Это правило из поставки.")
                : $"Ни одно правило не совпало, применён режим по умолчанию — {mode}."
                  + (decision.HadUnevaluableDomainRules
                      ? " Часть доменных правил проверить было нечем."
                      : string.Empty);
        }
        catch (Exception ex)
        {
            AnswerCard.Visibility = Visibility.Visible;
            AnswerMode.Text = "Правила не читаются";
            AnswerMode.Foreground = (Brush)FindResource("Danger");
            AnswerWhy.Text = ex.GetBaseException().Message;
        }
    }
}
