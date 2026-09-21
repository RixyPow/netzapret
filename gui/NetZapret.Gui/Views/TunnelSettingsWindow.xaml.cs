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
    /// <summary>
    /// Идёт заполнение, а не выбор человека.
    /// </summary>
    /// <remarks>
    /// Список поднимает событие выбора при показе состояния, и без заслонки
    /// открытие окна записывало бы настройку обратно и звало уведомление
    /// о перезапуске — на ровном месте.
    /// </remarks>
    private bool _filling;

    /// <summary>Что-нибудь изменилось, и движки стоит перезапустить.</summary>
    public bool Changed { get; private set; }

    public TunnelSettingsWindow()
    {
        InitializeComponent();

        Show(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void Show(AppSettings settings)
    {
        _filling = true;
        Mode.SelectedIndex = IndexOf(settings.Mode);
        _filling = false;

        Word(ForeignWord, settings.ForeignExitsOnly);
        Foreign.IsChecked = settings.ForeignExitsOnly;

        Word(BypassWord, settings.BypassWhenTunnelDead);
        Bypass.IsChecked = settings.BypassWhenTunnelDead;

        Word(WarpWord, settings.WarpEnabled);
        Warp.IsChecked = settings.WarpEnabled;

        ShowBypass(settings.BypassWhenTunnelDead);
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
    /// Каким пунктом списка показывается режим.
    /// </summary>
    /// <remarks>
    /// Двумя отдельными переводами — сюда и обратно, — и потому обязаны
    /// сходиться. Разойдись они, окно показывало бы один режим, а писало
    /// другой; заметить это можно было бы только по сломавшейся сети.
    /// Закреплено проверкой.
    /// </remarks>
    internal static int IndexOf(OperatingMode mode) => mode switch
    {
        OperatingMode.DesyncOnly => 1,
        OperatingMode.ProxyAll => 2,
        OperatingMode.ProxyStrict => 3,
        _ => 0,
    };

    /// <summary>
    /// Какой режим стоит за пунктом списка.
    /// </summary>
    /// <remarks>
    /// Неизвестный пункт даёт «выборочно», и это не лень, а выбор
    /// в безопасную сторону: там в туннель уходит только нужное,
    /// а российские сервисы работают. Ошибка в сторону «всё через VPN»
    /// сломала бы их молча.
    /// </remarks>
    internal static OperatingMode ModeAt(int index) => index switch
    {
        1 => OperatingMode.DesyncOnly,
        2 => OperatingMode.ProxyAll,
        3 => OperatingMode.ProxyStrict,
        _ => OperatingMode.Selective,
    };

    private void OnMode(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _filling || Mode.SelectedIndex < 0)
            return;

        var mode = ModeAt(Mode.SelectedIndex);

        Save(s => s with { Mode = mode }, mode switch
        {
            OperatingMode.DesyncOnly => "Туннель подниматься не будет — работает один десинк.",
            OperatingMode.ProxyAll => "В туннель уйдёт всё, кроме исключений на прямой проход.",
            OperatingMode.ProxyStrict => "В туннель уйдёт всё без исключений. Российские сервисы сломаются.",
            _ => "В туннель уйдёт только то, для чего он нужен.",
        });
    }

    private void OnForeign(object sender, RoutedEventArgs e) =>
        Save(s => s with { ForeignExitsOnly = Foreign.IsChecked == true },
            Foreign.IsChecked == true
                ? "Автоподбор будет брать только зарубежные выходы."
                : "Автоподбор снова берёт все выходы, включая отечественные.");

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
