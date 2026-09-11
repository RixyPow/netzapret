using System.IO;
using System.Net;
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

        // Проверки на консольную программу рядом здесь больше нет. Она стояла
        // с тех пор, когда окно звало её поднимать движки, и после перехода
        // на собственный супервизор ругалась на исправную установку: в поставке
        // консоли нет вовсе, а «запуск не сработает» — прямая неправда.
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

            if (strays.Count == 0)
            {
                var ghost = Ghost();

                return ghost is null
                    ? [Ok("Движков и адаптеров от прошлых запусков нет.")]
                    : [Bad($"В системе остался туннельный адаптер: {ghost}. Живого движка "
                        + "при этом нет — адаптер пережил его. Новый sing-box не сможет "
                        + "создать свой и будет падать с «Cannot create a file when that "
                        + "file already exists». Уберите адаптер в диспетчере устройств "
                        + "(«Сетевые адаптеры» → sing-tun Tunnel → Удалить) либо командой "
                        + "pnputil /remove-device, и запустите движки заново.")];
            }

            return [Bad($"Движки без присмотра: {string.Join(", ", strays)}. Они держат "
                    + "TUN-адаптер и WinDivert, и новый движок не поднимется — он будет "
                    + "числиться запущенным и не отвечать. Остановите движки и запустите "
                    + "заново: запуск из окна убирает такие сам.")];
        }
        catch (Exception ex)
        {
            return [Warn("Процессы не перечисляются: " + ex.GetBaseException().Message)];
        }
    }

    /// <summary>
    /// Туннельный адаптер, переживший свой движок.
    /// </summary>
    /// <remarks>
    /// Ищется по описанию, а не по имени: брошенный адаптер теряет имя
    /// <c>netzapret0</c> и остаётся в системе обычным Ethernet со своим
    /// номером, сохраняя описание «sing-tun Tunnel». Убитый жёстко sing-box
    /// его за собой не убирает — wintun рассчитывает на спокойный выход.
    /// </remarks>
    private static string? Ghost()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(a => a.Description.Contains("sing-tun", StringComparison.OrdinalIgnoreCase))
                .Select(a => $"{a.Name} ({a.Description})")
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // Не смогли посмотреть — молчим: догадка хуже отсутствия строки.
            return null;
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
        if (e.Key == System.Windows.Input.Key.Enter)
            Ask();
    }

    /// <summary>
    /// Прячет подсказку в поле, как только в нём что-то есть.
    /// </summary>
    /// <remarks>
    /// По вводу, а не по нажатию «Спросить». Прежде подсказки снимались
    /// внутри самого запроса, и всё время набора текст лежал поверх серой
    /// подписи: «Программа, discord.exe» и введённое имя читались друг
    /// сквозь друга. Нажатие клавиши для этого тоже не годится — вставка
    /// мышью его не поднимает.
    /// </remarks>
    private void OnAskChanged(object sender, TextChangedEventArgs e) => ShowHints();

    private void ShowHints()
    {
        HostHint.Visibility = Host.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        ExeHint.Visibility = Exe.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        AddressHint.Visibility = Address.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        PortHint.Visibility = Port.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
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

        ShowHints();

        var exe = Exe.Text.Trim();
        var addressText = Address.Text.Trim();

        if (host.Length == 0 && exe.Length == 0 && addressText.Length == 0)
        {
            Answer("Нечего спрашивать", "Warn",
                "Задайте хотя бы одно: имя, программу или адрес.");

            return;
        }

        // Имя проверяется на точку только когда оно и есть вопрос: с одним
        // лишь процессом или адресом пустое имя — законный случай.
        if (host.Length > 0 && !host.Contains('.'))
        {
            Answer("Это не похоже на имя сайта", "Warn",
                "Нужно что-то вроде web.whatsapp.com.");

            return;
        }

        IPAddress? address = null;

        if (addressText.Length > 0 && !IPAddress.TryParse(addressText, out address))
        {
            Answer("Это не похоже на адрес", "Warn",
                $"«{addressText}» не разбирается как IP-адрес.");

            return;
        }

        ushort port = 443;

        if (Port.Text.Trim().Length > 0 && !ushort.TryParse(Port.Text.Trim(), out port))
        {
            Answer("Это не похоже на порт", "Warn",
                $"«{Port.Text.Trim()}» не разбирается как номер порта.");

            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

            var connection = new ConnectionEvent
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = ProtocolKind.Tcp,

                // Выдуманный адрес не подставляется: если про него не спросили,
                // его нет. IPAddress.None — это 255.255.255.255, и он однажды
                // уже попал в список российских подсетей, отчего все сервисы
                // показывались идущими напрямую.
                RemoteAddress = address,
                RemotePort = port,
                ExecutablePath = exe.Length > 0 ? exe : null,
                Hostname = host.Length > 0 ? host : null,
                Direction = ConnectionDirection.Outbound,
            };

            var decision = engine.Evaluate(connection);

            var (mode, key) = decision.Mode switch
            {
                RoutingMode.Direct => ("напрямую", "Muted"),
                RoutingMode.Desync => ("через десинк", "Warn"),
                _ => ("через VPN", "Accent"),
            };

            // Спрошенное называется целиком: с тремя полями по одному имени
            // в ответе уже не понять, что именно проверяли.
            var asked = new List<string>();

            if (host.Length > 0)
                asked.Add(host);

            if (exe.Length > 0)
                asked.Add(exe);

            if (address is not null)
                asked.Add($"{address}:{port}");
            else if (host.Length > 0 || exe.Length > 0)
                asked.Add($"порт {port}");

            Answer($"{string.Join(" · ", asked)} → {mode}", key, decision.Rule is { } rule
                ? $"Сработало правило: {rule.Match.ToString().ToLowerInvariant()} «{rule.Value}»"
                  + (decision.Reason is null ? "." : $" — {decision.Reason}.")
                  + (rule.Source == RuleSource.User ? " Это ваше правило." : " Это правило из поставки.")
                : $"Ни одно правило не совпало, применён режим по умолчанию — {mode}."
                  + (decision.HadUnevaluableDomainRules
                      ? " Часть доменных правил проверить было нечем: имя не задано."
                      : string.Empty));
        }
        catch (Exception ex)
        {
            Answer("Правила не читаются", "Danger", ex.GetBaseException().Message);
        }
    }

    private void Answer(string title, string colorKey, string why)
    {
        AnswerCard.Visibility = Visibility.Visible;
        AnswerMode.Text = title;
        AnswerMode.Foreground = (Brush)FindResource(colorKey);
        AnswerWhy.Text = why;
    }
}
