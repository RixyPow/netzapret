using System.Windows;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;

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
/// WARP вернулся на вкладку (01.10): он стал выбором пути — либо WARP, либо
/// подписки, — и его выключатель стоит рядом с подписками, которые он ставит на паузу.
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

        // Не выше рабочей области экрана: при масштабе интерфейса 820 точек
        // окна выходили за край, и низ с кнопкой «Закрыть» уходил под панель
        // задач (владелец, 01.10).
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 40);

        Show(AppSettings.Load(AppSettings.DefaultPath));
    }

    /// <summary>
    /// Пояснение под строкой — пустое прячется целиком.
    /// </summary>
    /// <remarks>
    /// Пустая строка с полем сверху всё равно занимает место: строка карточки
    /// выходит выше, а выключатель съезжает ниже названия. Владелец 01.10
    /// увидел это, когда в «Куда идёт трафик» стало две таких строки.
    /// </remarks>
    private static void Line(System.Windows.Controls.TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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

        Line(RussianLine, engines.Tunnel
            ? string.Empty
            : "Действует, когда туннель включён.");

        Status.Text = engines.Complaint ?? string.Empty;

        Word(HideVpnWord, settings.HideVpnFromRussianApps);
        HideVpn.IsChecked = settings.HideVpnFromRussianApps;

        // Сказать, когда включённое ничего не делает: «без исключений»
        // отбрасывает все правила «напрямую», а без туннеля прятать не от чего.
        Line(HideVpnLine, !engines.Tunnel
            ? "Действует, когда туннель включён."
            : engines.IgnoreExclusions && settings.HideVpnFromRussianApps
                ? "Сейчас не действует: включено «Игнорировать исключения»."
                : string.Empty);


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

        Word(VerifyWord, settings.VerifyTraffic);
        Verify.IsChecked = settings.VerifyTraffic;

        Word(MeasureOnStartWord, settings.MeasureOnStart);
        MeasureOnStart.IsChecked = settings.MeasureOnStart;

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
            Choose(MemoryChoice, settings.ServerMemoryDays);

            MemoryLine.Text = settings.ServerMemoryClearedAt is { } cleared
                ? $"Очищена {cleared.ToLocalTime():dd.MM в HH:mm}"
                  + (settings.ServerMemoryDays > 0
                      ? $", следующая — {cleared.AddDays(settings.ServerMemoryDays).ToLocalTime():dd.MM}."
                      : ", сама не очищается.")
                : "Ещё не очищалась.";

            FragmentChoice.SelectedItem = FragmentChoice.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Tag == settings.TlsFragment.ToString()) ?? FragmentChoice.Items[0];

            // Адрес проверки: два известных — пунктами, прочее — «свой адрес»
            // с полем; пустой или негодный в файле — Cloudflare, как и считает Ping.
            var url = Ping.UrlOf(settings);
            string tag = url == Ping.Cloudflare ? "cloudflare" : url == Ping.Google ? "google" : "own";

            PingChoice.SelectedItem = PingChoice.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                .First(i => (string)i.Tag == tag);
            PingOwnRow.Visibility = tag == "own" ? Visibility.Visible : Visibility.Collapsed;
            PingOwn.Text = tag == "own" ? url : string.Empty;
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
            ServerMemoryDays = Value(MemoryChoice) is >= 0 and var days ? days : s.ServerMemoryDays,
        }, "Проверка серверов изменена.");
    }

    /// <summary>«Очистить» память замеров — и отсчёт срока автоочистки заново.</summary>
    private void OnClearMemory(object sender, RoutedEventArgs e)
    {
        try
        {
            ServerMemory.ClearNow(DateTimeOffset.Now);
            Show(AppSettings.Load(AppSettings.DefaultPath));

            Changed = true;
            Status.Text = "Память замеров очищена: задержки появятся со следующим замером, "
                + "нестабильные вернутся в автоподбор при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось очистить: " + ex.GetBaseException().Message;
        }
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

    private void OnHideVpn(object sender, RoutedEventArgs e) =>
        Save(s => s with { HideVpnFromRussianApps = HideVpn.IsChecked == true },
            HideVpn.IsChecked == true
                ? "Адрес у российских приложений будет домашним: двенадцать имён пойдут напрямую."
                : "Имена, по которым приложения узнают адрес, снова идут по общим правилам.");

    private void OnReplace(object sender, RoutedEventArgs e) =>
        Save(s => s with { ReplaceSilentServer = Replace.IsChecked == true },
            Replace.IsChecked == true
                ? "Выбранному серверу будет искаться замена, пока он молчит."
                : "Выбранный сервер не подменяется: молчит — туннель ждёт его.");

    private void OnVerify(object sender, RoutedEventArgs e)
    {
        bool on = Verify.IsChecked == true;

        Save(s => s with { VerifyTraffic = on },
            on
                ? "Проверка прохода включена: молчащий туннель будет виден."
                : "Проверка прохода выключена: туннель судится по открытому порту.");
    }

    private void OnFragment(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_showingChecks || !IsLoaded
            || FragmentChoice.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<TlsFragment>(tag, out var mode))
        {
            return;
        }

        Save(s => s with { TlsFragment = mode },
            mode switch
            {
                TlsFragment.Records => "Приветствие TLS серверу пойдёт несколькими записями.",
                TlsFragment.Packets => "Приветствие TLS серверу пойдёт несколькими пакетами — подключение станет медленнее.",
                _ => "Фрагментация выключена: рукопожатие с сервером как есть.",
            });
    }

    private void OnPingChoice(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_showingChecks || !IsLoaded || PingChoice.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string tag })
            return;

        // «Свой адрес» — сперва поле: пишется по «Сохранить», когда адрес введён.
        if (tag == "own")
        {
            PingOwnRow.Visibility = Visibility.Visible;
            PingOwn.Focus();
            Status.Text = "Введите адрес и нажмите «Сохранить».";
            return;
        }

        var url = tag == "google" ? Ping.Google : Ping.Cloudflare;

        Save(s => s with { PingUrl = url == Ping.Cloudflare ? null : url }, $"Адрес проверки — {url}.");
    }

    private void OnPingOwn(object sender, RoutedEventArgs e)
    {
        var url = PingOwn.Text.Trim();

        if (!Ping.IsUrl(url))
        {
            Status.Text = "Нужен полный адрес на http или https, например http://cp.cloudflare.com/generate_204.";
            return;
        }

        Save(s => s with { PingUrl = url }, $"Адрес проверки — {url}.");
    }

    private void OnPingOwnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            OnPingOwn(sender, e);
    }

    private void OnMeasureOnStart(object sender, RoutedEventArgs e)
    {
        bool on = MeasureOnStart.IsChecked == true;

        Word(MeasureOnStartWord, on);

        Save(s => s with { MeasureOnStart = on },
            on
                ? "Серверы будут замеряться сами через минуту после запуска программы."
                : "Серверы замеряются только кнопкой «Замерить все».");
    }

    private void OnBypass(object sender, RoutedEventArgs e)
    {
        bool on = Bypass.IsChecked == true;

        ShowBypass(on);

        Save(s => s with { BypassWhenTunnelDead = on },
            on
                ? "Обход включён: при мёртвых выходах сеть продолжит работать мимо туннеля."
                : "Обход выключен: при мёртвых выходах ничего не пойдёт мимо туннеля.");
    }

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
