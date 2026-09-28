using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetZapret.Core;

namespace NetZapret.Gui;

/// <summary>Одна подписка: имя, ссылка и то, раскрыта ли её папка.</summary>
public sealed class SubscriptionEntry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Ссылка. Пароль: не печатается, не пишется в журнал, не показывается.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Состояние показа, а не настройка; хранится, чтобы папки не захлопывались при каждом заходе.</summary>
    public bool Open { get; set; } = true;

    /// <summary>В работе — её серверы идут в пул движка (0.9.0).</summary>
    /// <remarks>
    /// Может быть пусто только в файле до 0.9.0: такие записи при чтении
    /// переводятся так, чтобы поведение не изменилось, — в работе та,
    /// что была действующей, остальные выключены (<see cref="SubscriptionBook.Load"/>).
    /// </remarks>
    public bool? InPool { get; set; }

    [JsonIgnore]
    public bool Working => InPool == true;
}

/// <summary>
/// Несколько подписок сразу.
/// </summary>
/// <remarks>
/// <para>
/// Отдельный файл, а не поле в <see cref="AppSettings"/>. Консоль знает одну
/// ссылку — <see cref="AppSettings.SubscriptionUrl"/>, — и по ней собирает
/// конфиг sing-box. Переучивать её ради окна значило бы править то, что
/// работает, поэтому здесь заведён свой список, а <c>SubscriptionUrl</c>
/// остаётся указателем — на первую подписку в работе. Обе стороны читают
/// одно и то же и не расходятся.
/// </para>
/// <para>
/// С 0.9.0 окно собирает движок из пула — всех подписок в работе
/// (<see cref="NetZapret.Subscriptions.SubscriptionPool"/>), и сервер можно
/// выбрать из любой из них. Выбор сервера из подписки вне работы ставит
/// её в работу — иначе конфиг собрался бы без этого сервера.
/// </para>
/// <para>
/// Файл лежит рядом с настройками и содержит пароли в открытом виде — ровно
/// как <c>netzapret.json</c> до него. Шифровать его нечем: ключ пришлось бы
/// хранить тут же, а хранилище учётных данных Windows привязано к пользователю
/// и не переживает переноса папки, ради которого программа и сделана
/// переносной.
/// </para>
/// </remarks>
public sealed class SubscriptionBook
{
    public List<SubscriptionEntry> Entries { get; set; } = [];

    /// <summary>Отдельные ключи (0.9.0) — пароли, как и ссылки: не печатаются и не пишутся в журнал.</summary>
    public List<string> Keys { get; set; } = [];

    /// <summary>Ключи в работе — идут в пул движка.</summary>
    public bool KeysInPool { get; set; } = true;

    /// <summary>Ключи, которые идут в пул: пусто, если выключены.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> PoolKeys => KeysInPool ? Keys : [];

    /// <summary>
    /// Пишет ключи и ставит признак «ключи есть» в настройки.
    /// </summary>
    /// <remarks>
    /// Признак нужен сборке и «Главной»: с одними ключами, без подписки
    /// и WARP, программа иначе решала бы, что выхода нет.
    /// </remarks>
    public static void SaveKeys(IReadOnlyList<string> keys, bool inPool)
    {
        var book = Load();
        book.Keys = [.. keys];
        book.KeysInPool = inPool;
        book.Save();

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        bool enabled = inPool && keys.Count > 0;

        if (settings.KeysEnabled != enabled)
            (settings with { KeysEnabled = enabled }).Save(AppSettings.DefaultPath);
    }

    public static string DefaultPath => Path.Combine("config", "subscriptions.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Читает список и подтягивает в него действующую подписку.
    /// </summary>
    /// <remarks>
    /// Подтягивает намеренно: у тех, кто настроился до появления этого файла,
    /// ссылка живёт только в <c>netzapret.json</c>, и без переноса раздел VPN
    /// показал бы пустоту человеку с работающим туннелем.
    /// </remarks>
    public static SubscriptionBook Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        SubscriptionBook book;

        try
        {
            book = File.Exists(target)
                ? JsonSerializer.Deserialize<SubscriptionBook>(File.ReadAllText(target), Options) ?? new()
                : new SubscriptionBook();
        }
        catch (Exception)
        {
            // Испорченный файл не повод не открыть раздел: действующая
            // подписка всё равно приедет из настроек строкой ниже.
            book = new SubscriptionBook();
        }

        book.Entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Url));

        // WARP успел побыть отдельной строкой списка и оказался в этой роли
        // вреден: конфиг собирается по одной ссылке, и выбор его выхода делал
        // действующим его, отключая рабочую подписку целиком. Теперь это
        // выключатель, а старая строка переносится в него и убирается.
        if (book.Entries.RemoveAll(e => e.Url.StartsWith("warp://", StringComparison.OrdinalIgnoreCase)) > 0)
        {
            try
            {
                var moved = AppSettings.Load(AppSettings.DefaultPath);

                (moved with
                {
                    WarpEnabled = true,

                    // Если действующей была она, указатель повис бы на ссылку,
                    // которой больше нет, и раздел показал бы «ни одна
                    // не действует» при живых подписках.
                    SubscriptionUrl = moved.SubscriptionUrl?.StartsWith("warp://", StringComparison.OrdinalIgnoreCase) == true
                        ? book.Entries.FirstOrDefault()?.Url
                        : moved.SubscriptionUrl,
                }).Save(AppSettings.DefaultPath);

                book.Save(target);
            }
            catch (Exception)
            {
                // Перенос — удобство. Не вышло, значит выключатель просто
                // окажется выключенным, и его включат руками.
            }
        }

        var active = AppSettings.Load(AppSettings.DefaultPath).SubscriptionUrl;

        if (!string.IsNullOrWhiteSpace(active) && !book.Entries.Any(e => Same(e.Url, active)))
        {
            book.Entries.Insert(0, new SubscriptionEntry
            {
                Name = book.Entries.Count == 0 ? "Основная" : "Перенесённая",
                Url = active,
            });
        }

        // Переход к пулу (0.9.0): у записей до него признака нет. В работу
        // встаёт та, что была действующей, остальные выключены — ровно
        // прежнее поведение, пока человек сам не включит вторую.
        // Указатель в настройках тоже всегда в работе: его ставит и консоль,
        // и выставленный ею адрес должен работать, а не лежать выключенным.
        foreach (var entry in book.Entries)
        {
            if (entry.InPool is null || (Same(entry.Url, active) && entry.InPool == false))
                entry.InPool = Same(entry.Url, active);
        }

        return book;
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(target, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
    }

    /// <summary>Действующая — та, чью ссылку читает консоль: первая в работе.</summary>
    public SubscriptionEntry? Active(AppSettings settings) =>
        Entries.FirstOrDefault(e => Same(e.Url, settings.SubscriptionUrl));

    /// <summary>Подписки в работе, в порядке списка: их серверы и есть пул.</summary>
    [JsonIgnore]
    public IReadOnlyList<SubscriptionEntry> Pool => Entries.Where(e => e.Working).ToList();

    /// <summary>
    /// Включает подписку в пул или выводит из него — и записывает.
    /// </summary>
    /// <remarks>
    /// Указатель <see cref="AppSettings.SubscriptionUrl"/> после этого — первая
    /// в работе: по нему консоль собирает свой конфиг, по нему окно судит,
    /// есть ли вообще выход. Никого в работе — указателя нет.
    /// </remarks>
    public static void SetWorking(string url, bool working)
    {
        var book = Load();

        foreach (var entry in book.Entries.Where(e => Same(e.Url, url)))
            entry.InPool = working;

        book.Save();

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var first = book.Pool.FirstOrDefault()?.Url;

        if (!Same(first, settings.SubscriptionUrl))
            (settings with { SubscriptionUrl = first }).Save(AppSettings.DefaultPath);
    }

    /// <summary>
    /// Ставит подписку в работу.
    /// </summary>
    /// <remarks>
    /// До 0.9.0 — «сделать действующей»: она заменяла прежнюю, и выбор
    /// сервера сбрасывался, потому что его тег мог остаться в прошлой.
    /// В пуле прежние серверы никуда не деваются, и выбор сохраняется.
    /// </remarks>
    public static void MakeActive(SubscriptionEntry entry) => SetWorking(entry.Url, true);

    /// <summary>Счёт для показа. Ссылки не разглашает — в том и смысл.</summary>
    public string Describe(AppSettings settings)
    {
        // Ключи — отдельным хвостом (0.9.0): подпиской они не являются.
        var keys = Keys.Count == 0 ? string.Empty
            : $" · ключей: {Keys.Count}" + (KeysInPool ? string.Empty : " (не в работе)");

        return DescribeSubscriptions(settings) + keys;
    }

    private string DescribeSubscriptions(AppSettings settings)
    {
        if (Entries.Count == 0)
            return Keys.Count > 0 ? "подписок нет" : "нет";

        // Запись без признака — ещё не прочитанная из файла, собранная в памяти:
        // в работе та, на которую смотрит указатель, как при переходе к пулу.
        var pool = Entries.Where(e => e.InPool ?? Same(e.Url, settings.SubscriptionUrl)).ToList();

        return Entries.Count == 1
            ? pool.Count == 0 ? "1, не в работе" : $"1: {pool[0].Name}"
            : pool.Count == 0
                ? $"{Entries.Count}, в работе ни одной"
                : $"{Entries.Count}, в работе: {string.Join(", ", pool.Select(e => $"«{e.Name}»"))}";
    }

    /// <summary>Имя, которого ещё нет в списке.</summary>
    public string FreeName()
    {
        for (int number = Entries.Count + 1; ; number++)
        {
            var name = $"Подписка {number}";

            if (!Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
}
