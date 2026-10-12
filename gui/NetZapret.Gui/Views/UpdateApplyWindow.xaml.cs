using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Updates;

namespace NetZapret.Gui.Views;

/// <summary>Что подменщику передала программа перед своим закрытием.</summary>
/// <param name="Downloaded">Подпись к шагу «Скачано»: «112 МБ через туннель».</param>
/// <param name="NotesFile">Чейнджлог новой версии; <c>null</c> — карточка «Что нового» пустая.</param>
public sealed record ApplyOptions(
    int ProcessId,
    string Source,
    string Target,
    string From,
    string To,
    string Downloaded,
    string? NotesFile)
{
    /// <summary>Разбирает то, что собрал <see cref="UpdateInstaller.ApplierArguments"/>.</summary>
    public static ApplyOptions? Parse(string[] args)
    {
        int at = Array.IndexOf(args, UpdateApplyWindow.Switch);

        if (at < 0 || args.Length < at + 7 || !int.TryParse(args[at + 1], out int processId))
            return null;

        return new ApplyOptions(
            processId,
            args[at + 2],
            args[at + 3],
            args[at + 4],
            args[at + 5],
            args[at + 6],
            args.Length > at + 7 && args[at + 7].Length > 0 ? args[at + 7] : null);
    }
}

/// <summary>
/// Окно подмены файлов — вместо чёрного cmd (владелец 12.10).
/// </summary>
/// <remarks>
/// <para>
/// Запущено из копии программы в runtime\update\updater: сама программа
/// к этому времени закрывается, и её exe надо заменить. Работа — в
/// <see cref="UpdateApplier"/>; окно только показывает шаги и даёт выбор,
/// когда что-то не заменилось: повторить, вернуть прежнюю версию или
/// запустить как есть.
/// </para>
/// <para>
/// Шаги 1–2 сделала ещё программа — здесь они уже готовы. Тема — встроенная
/// тёмная (<see cref="Themes.ApplyBuiltIn"/>): картинка или видео темы держали
/// бы файлы той самой папки, которую подменяем.
/// </para>
/// </remarks>
public partial class UpdateApplyWindow : Window
{
    /// <summary>Ключ роли подменщика.</summary>
    public const string Switch = UpdateInstaller.ApplierSwitch;

    private readonly ApplyOptions _options;
    private readonly UpdateApplier _applier;
    private readonly List<StepRow> _steps = [];
    private bool _busy;
    private DispatcherTimer? _closing;

    public UpdateApplyWindow(ApplyOptions options)
    {
        InitializeComponent();

        _options = options;
        _applier = new UpdateApplier(options.Source, options.Target,
            System.IO.Path.Combine(options.Target, UpdateInstaller.StagingDirectory, "backup"));

        Heading.Text = $"NetZapret {options.From} → {options.To}";
        NotesTitle.Text = $"Что нового в {options.To}";
        RollBackButton.Content = $"Вернуть {options.From}";

        string[] titles = ["Скачано", "Движки остановлены", "Программа закрыта", "Замена файлов", "Запуск новой версии"];

        for (int i = 0; i < titles.Length; i++)
            _steps.Add(StepRow.Add(Steps, $"{i + 1}. {titles[i]}", withProgress: i == 3));

        ShowNotes();

        // Посреди замены окно не закрывается: половина новых файлов
        // и половина прежних — хуже любой из двух версий.
        Closing += (_, e) =>
        {
            if (_busy)
                e.Cancel = true;
        };

        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        _busy = true;

        _steps[0].Show(StepState.Done, _options.Downloaded);
        _steps[1].Show(StepState.Done, "sing-box, winws2");
        _steps[2].Show(StepState.Running, "Жду, пока программа отпустит свои файлы");

        bool exited = await UpdateApplier.WaitForExitAsync(_options.ProcessId, TimeSpan.FromMinutes(1), CancellationToken.None);

        if (!exited)
        {
            _busy = false;
            _steps[2].Show(StepState.Failed, "Не закрылась за минуту");
            ShowError("Программа не закрылась",
                "NetZapret всё ещё работает — заменить его файлы нельзя.",
                "Закройте его через трей («Выход») и нажмите «Повторить». Файлы ещё не тронуты.",
                retry: true, rollBack: false, asIs: false, retryWait: true);
            return;
        }

        await Task.Run(_applier.StopLeftovers);
        _steps[2].Show(StepState.Done, "Все файлы освобождены");

        await CopyAsync();
    }

    private async Task CopyAsync()
    {
        _busy = true;
        ShowInfo("Не закрывайте окно — это займёт несколько секунд.");
        _steps[3].Show(StepState.Running, null);
        _steps[3].Progress(_applier.Done, _applier.Total);

        // В поток окна явно: Progress берёт контекст при создании, а он бывает
        // пустым (снимок из теста 12.10 упал на этом), и тогда отчёт шёл бы
        // в пул — мимо окна.
        var progress = new Progress<ApplyProgress>(p =>
            Dispatcher.BeginInvoke(() => _steps[3].Progress(p.Done, p.Total)));
        var failure = await Task.Run(() => _applier.CopyAsync(progress, CancellationToken.None));

        _busy = false;

        if (failure is not null)
        {
            _steps[3].Show(StepState.Failed, null);
            _steps[3].Progress(_applier.Done, _applier.Total, failed: true);

            var who = failure.Lockers.Count > 0
                ? "его держит другая программа: " + string.Join(", ", failure.Lockers.Select(FileLockers.Describe)) + "."
                : failure.Reason;

            ShowError("Не удалось заменить файл",
                $"Не заменился файл {failure.File} — {who}",
                $"Новые файлы уже на месте ({_applier.Done} из {_applier.Total}), этот остался прежним. "
                    + (failure.Lockers.Count > 0
                        ? "Закройте эту программу и нажмите «Повторить»."
                        : $"Нажмите «Повторить»; не поможет — «Вернуть {_options.From}»."),
                retry: true, rollBack: _applier.CanRollBack, asIs: true);
            return;
        }

        _steps[3].Show(StepState.Done, null);
        _steps[3].Progress(_applier.Total, _applier.Total);
        _steps[4].Show(StepState.Done, "Подготовка завершена");

        ShowDone($"Обновлено до {_options.To}", "Запускаю программу…", success: true);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (RetryButton.Tag is true)
        {
            RetryButton.Tag = null;
            await RunAsync();
            return;
        }

        await CopyAsync();
    }

    private async void OnRollBack(object sender, RoutedEventArgs e)
    {
        _busy = true;
        ShowInfo($"Возвращаю файлы {_options.From}…");

        int failed = await Task.Run(_applier.RollBack);

        _busy = false;

        if (failed > 0)
        {
            ShowError("Вернулось не всё",
                $"Не вернулось файлов: {failed}. Какие — в журнале.",
                "Перезагрузите Windows и скачайте архив прежней версии с GitHub — это вернёт её целиком.",
                retry: false, rollBack: false, asIs: true);
            return;
        }

        _steps[3].Show(StepState.Failed, $"Возвращена {_options.From}");
        ShowDone($"Возвращена {_options.From}", "Обновление отменено, файлы прежние. Запускаю программу…", success: false);
    }

    private void OnAsIs(object sender, RoutedEventArgs e) => LaunchAndClose();

    private void OnOpenNow(object sender, RoutedEventArgs e) => LaunchAndClose();

    private void OnJournal(object sender, RoutedEventArgs e) =>
        Open(System.IO.Path.Combine(_options.Target, UpdateInstaller.LogFile));

    private void OnAllNotes(object sender, RoutedEventArgs e) =>
        Open($"{About.Repository}/releases/tag/v{_options.To}");

    private void ShowInfo(string text)
    {
        InfoText.Text = text;
        InfoPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string title, string text, string hint, bool retry, bool rollBack, bool asIs, bool retryWait = false)
    {
        ErrorTitle.Text = title;
        ErrorText.Text = text;
        ErrorHint.Text = hint;

        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Tag = retryWait ? true : null;
        RollBackButton.Visibility = rollBack ? Visibility.Visible : Visibility.Collapsed;
        AsIsButton.Visibility = asIs ? Visibility.Visible : Visibility.Collapsed;

        InfoPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        DonePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowDone(string title, string text, bool success)
    {
        DoneTitle.Text = title;
        DoneText.Text = text;
        DoneMark.SetResourceReference(Border.BackgroundProperty, success ? "Accent" : "Warn");
        DoneGlyph.Text = success ? "" : "";

        InfoPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;

        // Секунды, чтобы глаз успел прочитать итог; «Открыть сейчас» — не ждать.
        _closing = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _closing.Tick += (_, _) => LaunchAndClose();
        _closing.Start();
    }

    private void LaunchAndClose()
    {
        _closing?.Stop();

        try
        {
            Process.Start(new ProcessStartInfo(System.IO.Path.Combine(_options.Target, "NetZapret.exe"))
            {
                WorkingDirectory = _options.Target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Программа не запустилась", ex.GetBaseException().Message,
                $"Запустите её сами: {_options.Target}\\NetZapret.exe.", retry: false, rollBack: false, asIs: false);
            return;
        }

        Close();
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нечем открыть — не повод ронять окно подмены.
        }
    }

    /// <summary>«Что нового»: разделы чейнджлога, у каждого — первая строка.</summary>
    private void ShowNotes()
    {
        string? text = null;

        try
        {
            if (_options.NotesFile is { } file && File.Exists(file))
                text = File.ReadAllText(file);
        }
        catch (Exception)
        {
        }

        var sections = ReleaseNotesText.Sections(text).Where(s => s.Category is not null).Take(4).ToList();

        foreach (var section in sections)
        {
            var category = section.Category!;
            var lead = section.Blocks.Select(b => b.Lead).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
                ?? section.Blocks.Select(b => b.Plain).FirstOrDefault(p => p.Length > 0 && !p.EndsWith(':'))
                ?? string.Empty;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var glyph = new TextBlock
            {
                Text = UpdateWindow.Glyphs.TryGetValue(category.Key, out var known) ? known : UpdateWindow.Glyphs["general"],
                FontSize = 18,
                Margin = new Thickness(0, 1, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = category.Name, FontWeight = FontWeights.SemiBold });

            var body = new TextBlock
            {
                Text = lead.Trim().TrimEnd('.'),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 38,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            };
            body.SetResourceReference(StyleProperty, "Caption");
            words.Children.Add(body);

            Grid.SetColumn(words, 1);
            row.Children.Add(glyph);
            row.Children.Add(words);
            Notes.Children.Add(row);
        }

        if (sections.Count == 0)
        {
            var none = new TextBlock { Text = "Чейнджлог — по ссылке ниже.", TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(StyleProperty, "Caption");
            Notes.Children.Add(none);
        }
    }
}

/// <summary>Состояние шага.</summary>
internal enum StepState { Pending, Running, Done, Failed }

/// <summary>Строка шага: кружок, название, подпись, у замены — счёт и полоса.</summary>
internal sealed class StepRow
{
    private readonly Border _mark;
    private readonly TextBlock _sub;
    private readonly TextBlock? _count;
    private readonly ProgressBar? _bar;

    private StepRow(Border mark, TextBlock sub, TextBlock? count, ProgressBar? bar)
    {
        _mark = mark;
        _sub = sub;
        _count = count;
        _bar = bar;
    }

    public static StepRow Add(Panel host, string title, bool withProgress)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var mark = new Border { Width = 26, Height = 26, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold });

        TextBlock? count = null;
        ProgressBar? bar = null;

        if (withProgress)
        {
            count = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(count, 1);
            head.Children.Add(count);
        }

        words.Children.Add(head);

        var sub = new TextBlock { Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        sub.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        words.Children.Add(sub);

        if (withProgress)
        {
            bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 8, 0, 0), Minimum = 0, BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
            bar.SetResourceReference(Control.BackgroundProperty, "Border");
            bar.SetResourceReference(Control.ForegroundProperty, "AccentFill");
            words.Children.Add(bar);
        }

        Grid.SetColumn(words, 1);
        grid.Children.Add(mark);
        grid.Children.Add(words);
        host.Children.Add(grid);

        var row = new StepRow(mark, sub, count, bar);
        row.Show(StepState.Pending, null);
        return row;
    }

    public void Show(StepState state, string? sub)
    {
        _mark.Child = state switch
        {
            StepState.Done => Circle("Accent", ""),
            StepState.Failed => Circle("Danger", ""),
            StepState.Running => Spinner(),
            _ => Circle("Border", null),
        };

        if (sub is not null)
        {
            _sub.Text = sub;
            _sub.Visibility = Visibility.Visible;
        }
    }

    public void Progress(int done, int total, bool failed = false)
    {
        if (_bar is null || _count is null)
            return;

        _bar.Visibility = Visibility.Visible;
        _bar.Maximum = Math.Max(1, total);
        _bar.Value = done;
        _bar.SetResourceReference(Control.ForegroundProperty, failed ? "Danger" : done >= total ? "Accent" : "AccentFill");
        _count.Text = $"{done} из {total}";
    }

    private static Border Circle(string fill, string? glyph)
    {
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13) };
        circle.SetResourceReference(Border.BackgroundProperty, fill);

        if (glyph is not null)
        {
            var mark = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            circle.Child = mark;
        }

        return circle;
    }

    /// <summary>Кольцо с разрывом, вращается — пока шаг идёт.</summary>
    private static FrameworkElement Spinner()
    {
        var ring = new Ellipse
        {
            Width = 24,
            Height = 24,
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection([18, 7]),
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        ring.SetResourceReference(Shape.StrokeProperty, "AccentFill");

        var turn = new RotateTransform();
        ring.RenderTransform = turn;
        turn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });

        return ring;
    }
}
