using System.ComponentModel;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>Что известно о том, отвечает ли адрес записи.</summary>
public enum HostsHealth
{
    Unknown,
    Alive,
    Dead,

    /// <summary>Не проверялся: адрес IPv6 в сети без IPv6.</summary>
    Untested,
}

/// <summary>Отбор таблицы — кнопка «Все записи ▾».</summary>
public enum HostsShow
{
    All,
    Ours,
    Foreign,
    Off,
    Dead,
    Repeats,
}

/// <summary>Строка таблицы раздела «Файл hosts»: имя из одной строки файла.</summary>
/// <remarks>
/// Живость и выбор меняются на месте, без пересборки списка: проверка
/// приходит по адресу за адресом, и пересобирать восемьсот строк на каждый
/// ответ значило бы занимать поток окна десятки раз подряд.
/// </remarks>
public sealed class HostsLine(HostsRow row) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public HostsRow Row { get; } = row;

    public string Name => Row.Name;

    public string Address => Row.Address;

    public string Source => Row.Source ?? "неизвестно";

    public string SourceHint => Row.Source is null
        ? "Строка вне всех блоков: её поставил кто-то без подписи — вы сами, антивирус или программа, которой уже нет."
        : Row.Ours
        ? "Наш блок: эти записи поставила NetZapret."
        : $"Строка лежит в блоке «{Row.Source}» — его ведёт эта программа и может вернуть записи при следующей своей правке.";

    public string Kind => Row.Kind switch
    {
        HostsPinKind.Service => "адрес сервиса",
        HostsPinKind.Stub => "заглушка",
        _ => "посредник",
    };

    public string KindHint => Row.Kind switch
    {
        HostsPinKind.Service =>
            "Адрес самого сервиса: пин только выбирает незакрытый адрес, а имя провайдер режет и там. "
            + "Щит десинка такое имя не берёт — его обрабатывает рецепт.",
        HostsPinKind.Stub =>
            "Петля или нули: имя закрыто намеренно, открываться оно не должно.",
        _ when Row.Ours =>
            "Посредник — чужой узел, через который ходит имя (XBOX DNS, Comss, GeoHide). "
            + "Щит десинка пропускает такое имя без обработки: рецепт, выверенный на настоящем сервисе, "
            + "рвёт соединение с посредником. Если адрес — сам сервис, смените вид в меню строки.",
        _ =>
            "Чужую строку щит десинка считает посредником и пропускает без обработки: пометку "
            + "«адрес сервиса» ставит только NetZapret, своей записью.",
    };

    /// <summary>Строка под именем: почему запись не действует либо её подпись.</summary>
    public string Sub =>
        Row.Repeat ? "повтор — действует запись выше"
        : Note is { Length: > 0 } note ? note
        : string.Empty;

    /// <summary>Подпись строки без нашей служебной пометки — та видна колонкой «Вид».</summary>
    public string? Note => Row.Note?.Replace(HostsEditor.DesyncMark, string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', ',', ';');

    public bool HasSub => Sub.Length > 0;

    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;

            _selected = value;
            Changed(nameof(Selected));
        }
    }

    private HostsHealth _health;

    public HostsHealth Health
    {
        get => _health;
        set
        {
            if (_health == value)
                return;

            _health = value;
            Changed(nameof(Health));
            Changed(nameof(State));
            Changed(nameof(StateHint));
        }
    }

    /// <summary>Для кружка: выключенная и заглушка важнее живости.</summary>
    public string State =>
        !Row.Enabled ? "Off"
        : Row.Kind == HostsPinKind.Stub ? "Stub"
        : Health.ToString();

    public string StateHint => State switch
    {
        "Off" => "Запись выключена: строка в файле под решёткой, Windows её не читает.",
        "Stub" => "Заглушка — проверять нечего.",
        nameof(HostsHealth.Alive) => "Адрес отвечает на 443.",
        nameof(HostsHealth.Dead) => "Адрес молчит на 443: пин ведёт в никуда и выглядит блокировкой.",
        nameof(HostsHealth.Untested) => "Не проверялся: в этой сети нет IPv6.",
        _ => "Не проверен — «Проверить адреса».",
    };

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Отбор строк файла hosts: поиск и кнопка «Все записи ▾».
/// </summary>
/// <remarks>
/// <para>
/// Заведён по просьбе владельца 21.09. На его машине в файле сто три наши
/// записи и семьсот восемьдесят две чужие — глазами такое не перебрать,
/// а искать в hosts приходится ровно тогда, когда что-то сломалось
/// и времени нет.
/// </para>
/// <para>
/// Ищется и по имени, и по адресу. Второе не прихоть: вопрос «кто у меня
/// прибит на 163.70.151.174» возникает не реже обратного — так ловятся
/// устаревшие адреса, оставшиеся от давно сменившейся сети доставки.
/// </para>
/// <para>
/// Отдельно от вкладки затем, что это её единственная чистая часть.
/// Проверить отбор можно без окна, а обработчик ввода — нет.
/// </para>
/// </remarks>
internal static class HostsFilter
{
    /// <summary>Подходит ли строка под запрос.</summary>
    /// <remarks>
    /// Пустой запрос подходит всем: это не «ничего не найдено», а «не ищем».
    /// </remarks>
    public static bool Matches(HostsLine line, string? needle)
    {
        if (!Searching(needle))
            return true;

        var wanted = needle!.Trim();

        return Has(line.Name, wanted)
            || Has(line.Address, wanted)
            || Has(line.Source, wanted)
            || Has(line.Kind, wanted)
            || Has(line.Row.Note, wanted);
    }

    public static bool Shows(HostsLine line, HostsShow show) => show switch
    {
        HostsShow.Ours => line.Row.Ours,
        HostsShow.Foreign => !line.Row.Ours,
        HostsShow.Off => !line.Row.Enabled,
        HostsShow.Dead => line.Row.Enabled && line.Health == HostsHealth.Dead,
        HostsShow.Repeats => line.Row.Repeat,
        _ => true,
    };

    /// <param name="source">Источник из карточки «Источники»; <c>null</c> — все.</param>
    public static IReadOnlyList<HostsLine> Apply(
        IEnumerable<HostsLine> lines,
        string? needle,
        HostsShow show = HostsShow.All,
        string? source = null) =>
        lines.Where(line => Shows(line, show)
                && (source is null || line.Source == source)
                && Matches(line, needle))
            .ToList();

    public static string Word(HostsShow show) => show switch
    {
        HostsShow.Ours => "Наши",
        HostsShow.Foreign => "Чужие",
        HostsShow.Off => "Отключённые",
        HostsShow.Dead => "Молчат",
        HostsShow.Repeats => "Повторы",
        _ => "Все записи",
    };

    /// <summary>
    /// Ищем ли мы вообще.
    /// </summary>
    /// <remarks>
    /// Пробелы не в счёт. Иначе случайный пробел в поле прятал бы весь
    /// список и выглядел бы поломкой вкладки.
    /// </remarks>
    public static bool Searching(string? needle) => !string.IsNullOrWhiteSpace(needle);

    private static bool Has(string? where, string what) =>
        where is not null && where.Contains(what, StringComparison.OrdinalIgnoreCase);
}
