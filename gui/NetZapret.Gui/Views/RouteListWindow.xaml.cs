using System.Windows;
using NetZapret.Core.Rules;

namespace NetZapret.Gui.Views;

/// <summary>
/// «Добавить списком»: несколько сайтов разом, один путь на всех (идея №14, 19.10).
/// </summary>
/// <remarks>
/// Решает не окно, а <see cref="RouteListInput"/>: что сайт, что уже своё, что
/// уже идёт тем же путём, что имя самой программы. Окно показывает итог при
/// каждой правке — до нажатия видно, что запишется и что нет.
/// </remarks>
public partial class RouteListWindow : Window
{
    private readonly RuleEngine? _engine;
    private readonly IReadOnlyList<UserRuleEntry> _own;
    private readonly IReadOnlyCollection<string> _reserved;

    /// <summary>Что записать; <c>null</c> — отказались.</summary>
    public RouteListPlan? Plan { get; private set; }

    /// <summary>Выбранный путь.</summary>
    public RoutingMode Mode { get; private set; } = RoutingMode.Direct;

    /// <summary>Папка; <c>null</c> — без папки.</summary>
    public string? Folder { get; private set; }

    /// <param name="text">Вставленное — сразу в поле.</param>
    /// <param name="engine">Движок правил последней сборки «Маршрутов»; <c>null</c> — ещё не собран.</param>
    /// <param name="own">Свои правила файла.</param>
    /// <param name="folder">Папка, в которую добавляли («Добавить маршрут в эту папку»).</param>
    /// <param name="reserved">Имена программ: папка с тем же именем слилась бы с программой.</param>
    public RouteListWindow(string text, RuleEngine? engine, IReadOnlyList<UserRuleEntry> own,
        string? folder, IReadOnlyCollection<string> reserved)
    {
        InitializeComponent();

        _engine = engine;
        _own = own;
        _reserved = reserved;

        ListBox.Text = text;
        FolderBox.Text = folder ?? string.Empty;

        _ready = true;
        ShowPlan();

        Loaded += (_, _) =>
        {
            ListBox.Focus();
            ListBox.CaretIndex = ListBox.Text.Length;
        };
    }

    /// <summary>Поля на месте: правки, пришедшие при разборе разметки, считать нечем.</summary>
    private readonly bool _ready;

    private RoutingMode Chosen =>
        PathVpn.IsChecked == true ? RoutingMode.Proxy
        : PathDesync.IsChecked == true ? RoutingMode.Desync
        : RoutingMode.Direct;

    private RouteListPlan Build() =>
        RouteListInput.Plan(ListBox.Text, _engine, _own, Chosen, toSite: ToSite.IsChecked == true);

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
            ShowPlan();
    }

    private void ShowPlan()
    {
        var plan = Build();
        var mode = Chosen;
        var lines = new List<string>();

        lines.Add(plan.Count == 0
            ? "Добавлять нечего."
            : $"Добавится: {plan.Count}"
                + (plan.Overrides.Count > 0
                    ? $" — из них {plan.Overrides.Count} сейчас идут другим путём по маршруту сервиса, своё правило его перекроет ({Some(plan.Overrides)})"
                    : string.Empty)
                + ".");

        if (plan.AlreadyOwn.Count > 0)
            lines.Add($"Уже есть своим правилом, путь меняется в его строке: {Some(plan.AlreadyOwn)}.");

        if (plan.SameRoute.Count > 0)
            lines.Add($"Уже идут {Word(mode)} по маршруту сервиса — пропущены: {Some(plan.SameRoute)}.");

        if (plan.ProgramOwn.Count > 0)
            lines.Add($"Имена самой программы (прокси Telegram, проверка сети Windows) — пропущены: {Some(plan.ProgramOwn)}.");

        if (plan.NotNames.Count > 0)
            lines.Add($"Не похоже на сайт или адрес: {Some(plan.NotNames)}.");

        // Десинк без рецепта решает пресет: имя, которого нет в его списках,
        // не трогает никто (подсказка «Маршрутов», 19.10 — 7tv так и стоял).
        if (mode == RoutingMode.Desync && plan.Count > 0)
            lines.Add("Десинк без рецепта: сайт, которого нет в списках пресета, он не трогает. Рецепт выбирается потом в строке сайта.");

        Summary.Text = string.Join("\n", lines);
        Confirm.Content = plan.Count > 0 ? $"Добавить {plan.Count}" : "Добавить";
        Confirm.IsEnabled = plan.Count > 0;
    }

    /// <summary>Первые несколько — и сколько ещё.</summary>
    private static string Some(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" и ещё {names.Count - 5}" : string.Empty);

    private static string Word(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "через VPN",
        RoutingMode.Desync => "десинком",
        _ => "напрямую",
    };

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();

        if (folder.Length > FolderNameWindow.MaxLength)
        {
            Summary.Text = $"Название папки — не длиннее {FolderNameWindow.MaxLength} знаков.";
            return;
        }

        if (_reserved.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Summary.Text = $"«{folder}» — папка программы, в неё кладутся только её домены и адреса. Выберите другое название.";
            return;
        }

        Plan = Build();
        Mode = Chosen;
        Folder = folder.Length == 0 ? null : folder;
        DialogResult = Plan.Count > 0;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
