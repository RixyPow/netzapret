using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Одна строка отчёта диагностики.</summary>
/// <remarks>
/// Цвет — именем ресурса, а кисть берётся при отрисовке: проверки идут
/// в фоне (замер 28.09: 0,4 с), а ресурс окна из фона не прочитать.
/// </remarks>
public sealed record DoctorLine(string Text, string Kind, string? ActionLabel = null, Action? Act = null)
{
    public Brush Color => (Brush)Application.Current.FindResource(Kind);
}

/// <summary>Раздел отчёта.</summary>
public sealed record DoctorGroup(string Title, IReadOnlyList<DoctorLine> Lines);

/// <summary>
/// Диагностика: всё, от чего зависит работа, разом.
/// </summary>
/// <remarks>
/// <para>
/// Сами проверки — в <see cref="Doctor"/>: те же уходят в отчёт для разбора
/// и в <c>nz doctor</c>. Здесь только цвета и кнопка у строки.
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

    /// <remarks>
    /// Проверки — в фоне. Замер 28.09: все вместе 0,4 с, и прежде раздел
    /// всё это время стоял пустым, а окно не отзывалось (владелец: «диагностика
    /// долго является пустой»). Теперь раздел открывается сразу со строкой
    /// «Смотрю…», а список приходит следом.
    /// </remarks>
    private async void Run()
    {
        RunButton.IsEnabled = false;
        Status.Text = "Смотрю…";

        try
        {
            var sections = await Task.Run(() => Doctor.Run(AppSettings.Load(AppSettings.DefaultPath)));

            Sections.ItemsSource = sections
                .Select(s => new DoctorGroup(s.Title, s.Lines.Select(Line).ToList()))
                .ToList();

            Motion.Arrive(Sections, dy: 0, ms: 200);

            Status.Text = Doctor.Verdict(sections);
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

    private DoctorLine Line(DoctorCheck check) => new(
        check.Text,
        check.Level switch
        {
            DoctorLevel.Bad => "Danger",
            DoctorLevel.Warn => "Warn",
            _ => "Accent",
        },
        check.ActionLabel,
        check.Open is { } console ? () => OpenConsole(console) : null);

    private void OnLineAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DoctorLine { Act: { } act })
            act();
    }

    /// <summary>
    /// Открывает окно сертификатов Windows.
    /// </summary>
    /// <remarks>
    /// Удаляет человек сам: хранилище доверия — системная настройка
    /// безопасности, и у удаления есть цена (сайты банков). Программа
    /// показывает, где лежит, и не решает за него.
    /// </remarks>
    private void OpenConsole(string console)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(console) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = $"Не удалось открыть {console}: " + ex.GetBaseException().Message;
        }
    }

    private void OnHostKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            Ask();
    }

    private void OnAsk(object sender, RoutedEventArgs e) => Ask();

    private void OnLog(object sender, RoutedEventArgs e) => LogWindow.Open();

    /// <summary>
    /// Собирает отчёт для разбора и показывает его в проводнике.
    /// </summary>
    /// <remarks>
    /// Сборка и вычистка — в библиотеке (<see cref="SupportReport"/>), ссылки
    /// подписок она находит сама. Окно только открывает папку с готовым файлом,
    /// чтобы его сразу можно было перетащить в чат.
    /// </remarks>
    private async void OnReport(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        ReportValue.Visibility = Visibility.Visible;
        ReportValue.Text = "Собираю…";

        try
        {
            var result = await Task.Run(() => SupportReport.Create(MainWindow.Version(), machine: true));

            ReportValue.Text = $"Готово: {Path.GetFileName(result.Path)} — в папке reports. "
                + $"Внутри: {string.Join(", ", result.Files)}.";

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{result.Path}\"");
            }
            catch (Exception)
            {
                // Проводник не открылся — путь назван выше, файл на месте.
            }
        }
        catch (Exception ex)
        {
            ReportValue.Text = "Не собрался: " + ex.Message;
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    }

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

            var engine = RuleSetLoader.LoadFor(settings);

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
                  + rule.Source switch
                  {
                      RuleSource.User => " Это ваше правило.",
                      RuleSource.Setting => " Это выключатель «Прятать VPN от российских приложений» "
                          + "в «Настройках туннеля».",
                      _ => " Это правило из поставки.",
                  }
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
