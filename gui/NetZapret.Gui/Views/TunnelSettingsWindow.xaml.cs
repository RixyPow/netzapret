using System.Windows;
using NetZapret.Core;
using NetZapret.Core.Rules;

namespace NetZapret.Gui.Views;

/// <summary>
/// Настройки туннеля отдельным окном.
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 21.09. Вкладка «VPN» разрослась: выключатель, выбор
/// сервера, автоподбор по зарубежным, добавление подписки, обход туннеля,
/// WARP — и под всем этим список подписок, ради которого на вкладку
/// и заходят. Настройки оттесняли его за нижний край.
/// </para>
/// <para>
/// Разделено по тому, как часто открывают: на вкладке подписки, серверы
/// и замеры, здесь — то, что задают однажды.
/// </para>
/// <para>
/// Вид взят у клиента Happ, и взято там ровно три приёма. Одна карточка
/// на группу вместо карточки на настройку — наши пять настроек занимали
/// пятьсот точек высоты. Справа состояние, а не действие: кнопка
/// с подписью-действием подводила нас дважды, и оба раза владелец говорил,
/// что нужный вариант пропал. Пояснений в строках нет — им место под
/// значком «i».
/// </para>
/// </remarks>
public partial class TunnelSettingsWindow : Window
{

    /// <summary>Что-нибудь изменилось, и движки стоит перезапустить.</summary>
    public bool Changed { get; private set; }

    public TunnelSettingsWindow()
    {
        InitializeComponent();

        Show(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void Show(AppSettings settings)
    {
        var engines = settings.Engines;

        Word(RussianWord, engines.IgnoreExclusions);
        Russian.IsChecked = engines.IgnoreExclusions;

        // Имеет смысл только при туннеле: без него везти некуда. Живой
        // выключатель у того, чего нет, обещает действие, которого не будет.
        //
        // С 23.09 — при любом туннеле, а не только забравшем всё: настройка
        // сама отправляет в туннель всё, в том числе при включённом десинке.
        Russian.IsEnabled = engines.Tunnel;

        RussianLine.Text = engines.Tunnel
            ? string.Empty
            : "Действует, когда туннель включён.";

        Status.Text = engines.Complaint ?? string.Empty;

        Word(ForeignWord, settings.ForeignExitsOnly);
        Foreign.IsChecked = settings.ForeignExitsOnly;

        Word(ReplaceWord, settings.ReplaceSilentServer);
        Replace.IsChecked = settings.ReplaceSilentServer;

        // Без выбранного сервера настройке подменять нечего — но выключатель
        // живой: её ставят заранее, до выбора сервера.
        //
        // Замену ищет сторож, а он молчит при выключенной проверке: сказать
        // об этом надо здесь же, иначе включённая настройка не делает ничего
        // и не объясняет почему (у владельца 30.09 стояло «не проверять»).
        ReplaceLine.Text = settings.ExitCheckSeconds <= 0
            ? "Проверка подключённого сервера выключена — замену искать некому. Включите её ниже."
            : string.IsNullOrWhiteSpace(settings.PreferredServer)
            ? "Сейчас сервер не выбран — работает автоподбор, он ищет замену сам."
            : settings.ReplaceSilentServer
                ? $"Пока «{settings.PreferredServer}» молчит, трафик пойдёт через другой сервер."
                : $"Пока «{settings.PreferredServer}» молчит, туннель ждёт его.";

        Word(BypassWord, settings.BypassWhenTunnelDead);
        Bypass.IsChecked = settings.BypassWhenTunnelDead;

        Word(WarpWord, settings.WarpEnabled);
        Warp.IsChecked = settings.WarpEnabled;

        ShowBypass(settings.BypassWhenTunnelDead);
        ShowChecks(settings);
    }

    /// <summary>Показ идёт — изменение выбора не запись.</summary>
    private bool _showingChecks;

    /// <summary>Ставит выпадающие списки проверки серверов по настройкам.</summary>
    /// <remarks>
    /// Значение, которого нет в списке (правлено руками в файле), ставит
    /// ближайшее — иначе список показал бы пустоту при работающей настройке.
    /// </remarks>
    private void ShowChecks(AppSettings settings)
    {
        _showingChecks = true;

        try
        {
            Choose(ExitCheckChoice, settings.ExitCheckSeconds);
            Choose(FullCheckChoice, settings.FullCheckMinutes);
            Choose(PerEntryChoice, settings.AutoPickPerEntry);
        }
        finally
        {
            _showingChecks = false;
        }

        static void Choose(System.Windows.Controls.ComboBox box, int value) =>
            box.SelectedItem = box.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .OrderBy(i => Math.Abs(int.Parse((string)i.Tag) - value))
                .First();
    }

    private void OnCheckSetting(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_showingChecks || !IsLoaded)
            return;

        static int Value(System.Windows.Controls.ComboBox box) =>
            box.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? int.Parse(tag) : -1;

        Save(s => s with
        {
            ExitCheckSeconds = Value(ExitCheckChoice) is >= 0 and var sec ? sec : s.ExitCheckSeconds,
            FullCheckMinutes = Value(FullCheckChoice) is > 0 and var min ? min : s.FullCheckMinutes,
            AutoPickPerEntry = Value(PerEntryChoice) is >= 0 and var n ? n : s.AutoPickPerEntry,
        }, "Проверка серверов изменена.");
    }

    /// <summary>
    /// Называет состояние словом рядом с тумблером.
    /// </summary>
    /// <remarks>
    /// Одного вида мало: включённое от выключенного у тумблера отличается
    /// положением кружка и оттенком, а это ровно те два признака, что
    /// теряются при беглом взгляде и исчезают у тех, кто плохо различает
    /// цвета. Оттуда же, из Happ: там рядом с каждым тумблером стоит
    /// «Вкл.» или «Выкл.».
    /// </remarks>
    private static void Word(System.Windows.Controls.TextBlock where, bool on) =>
        where.Text = on ? "вкл." : "выкл.";

    private void ShowBypass(bool on)
    {
        BypassLine.Text = on
            ? "При мёртвых выходах трафик пойдёт открыто и с домашнего адреса."
            : "При мёртвых выходах сеть не работает, но мимо туннеля не идёт ничего.";

        BypassInfo.Content = on
            ? "Если выход не ответит три проверки подряд, трафик пойдёт мимо туннеля, "
              + "чтобы не легла вся сеть. Вернётся в туннель сам, как только выход оживёт.\n\n"
              + "Десинк при этом работает как обычно: обход касается только того, "
              + "что шло через туннель, и закрытые сайты на это время останутся закрытыми."
            : "При мёртвых выходах трафик так и будет уходить в туннель — то есть в никуда. "
              + "Это выбор в пользу скрытности: ничего не пойдёт мимо туннеля даже ценой "
              + "неработающей сети.\n\nВключайте, если пользуетесь программой ради обхода "
              + "блокировок, а не ради скрытности.";
    }

    /// <summary>
    /// Записывает выбор движков.
    /// </summary>
    /// <remarks>
    /// Через <see cref="AppSettings.With(EngineChoice)"/>, а не правкой полей
    /// по одному: тот пишет заодно и выведенный режим. На языке режимов
    /// говорят конфиг движка, отчёты и консоль, и оставленный отставшим
    /// он развёл бы показания — окно говорило бы одно, движок делал другое.
    /// </remarks>
    private void Choose(Func<EngineChoice, EngineChoice> change, string said)
    {
        Save(s => s.With(change(s.Engines)), said);
    }

    private void OnRussian(object sender, RoutedEventArgs e) =>
        Choose(c => c with { IgnoreExclusions = Russian.IsChecked == true },
            Russian.IsChecked == true
                ? "Всё пойдёт через туннель, исключения не действуют."
                : "Исключения снова действуют: «напрямую» и «десинк» — мимо туннеля.");

    private void OnForeign(object sender, RoutedEventArgs e) =>
        Save(s => s with { ForeignExitsOnly = Foreign.IsChecked == true },
            Foreign.IsChecked == true
                ? "Автоподбор будет брать только зарубежные выходы."
                : "Автоподбор снова берёт все выходы, включая отечественные.");

    private void OnReplace(object sender, RoutedEventArgs e) =>
        Save(s => s with { ReplaceSilentServer = Replace.IsChecked == true },
            Replace.IsChecked == true
                ? "Выбранному серверу будет искаться замена, пока он молчит."
                : "Выбранный сервер не подменяется: молчит — туннель ждёт его.");

    private void OnBypass(object sender, RoutedEventArgs e)
    {
        bool on = Bypass.IsChecked == true;

        ShowBypass(on);

        Save(s => s with { BypassWhenTunnelDead = on },
            on
                ? "Обход включён: при мёртвых выходах сеть продолжит работать мимо туннеля."
                : "Обход выключен: при мёртвых выходах ничего не пойдёт мимо туннеля.");
    }

    private void OnWarp(object sender, RoutedEventArgs e) =>
        Save(s => s with { WarpEnabled = Warp.IsChecked == true },
            Warp.IsChecked == true
                ? "WARP добавлен к серверам действующей подписки."
                : "WARP выключен.");

    /// <summary>
    /// Пишет изменение и говорит о нём.
    /// </summary>
    /// <remarks>
    /// Настройки перечитываются перед каждой записью, а не держатся в поле.
    /// Окно живёт, пока его не закрыли, и за это время их мог поменять
    /// кто-то ещё — вкладка, консоль, правка файла руками. Записав своё
    /// поверх устаревшего снимка, мы бы молча отменили чужое.
    /// </remarks>
    private void Save(Func<AppSettings, AppSettings> change, string said)
    {
        try
        {
            var next = change(AppSettings.Load(AppSettings.DefaultPath));

            next.Save(AppSettings.DefaultPath);
            Show(next);

            Changed = true;
            Status.Text = said + " Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
