using NetZapret.Core;
using NetZapret.Subscriptions;

namespace NetZapret.Mobile.Data;

/// <summary>Подписка в книге: имя, ссылка-пароль и «в работе».</summary>
/// <param name="Url">Пароль: не показывается, не пишется в журнал и в сообщения.</param>
internal sealed record SubscriptionItem(string Name, string Url, bool Working);

/// <summary>
/// Книга подписок (<c>config/subscriptions.json</c>) — то, что интерфейс телефона из неё берёт.
/// </summary>
/// <remarks>
/// Узкий шов над <see cref="SubscriptionBook"/> из библиотеки подписок: разделы
/// телефона видят только то, что им нужно, а книга одна на окно Windows,
/// сборку конфига и телефон — правка в ней доходит до всех.
/// </remarks>
internal interface ISubscriptionStore
{
    IReadOnlyList<SubscriptionItem> Entries();

    IReadOnlyList<string> Keys();

    bool KeysInPool();

    /// <summary>Добавить подписку; та же ссылка второй раз — только имя и «в работе».</summary>
    void Add(string name, string url, bool working);

    void Remove(string url);

    /// <summary>«В работе» и первая из пула — в <c>AppSettings.SubscriptionUrl</c>, как у окна.</summary>
    void SetWorking(string url, bool working);

    /// <summary>Отдельные ключи целиком и «в работе» у их папки.</summary>
    void SaveKeys(IReadOnlyList<string> keys, bool inPool);

    /// <summary>Строка «Подписки» на «Главной» — без ссылок.</summary>
    string Describe(AppSettings settings);

    string FreeName();
}

internal static class SubscriptionStores
{
    public static ISubscriptionStore Store { get; set; } = new LibraryBook();
}

/// <summary>
/// Обёртка над <see cref="SubscriptionBook"/>: чтение и запись — её же, как у окна Windows.
/// </summary>
/// <remarks>
/// Замок — потому что разделы пишут книгу из фоновых задач (обновление
/// подписки, добавление), а книга читается и пишется целиком: две записи
/// разом потеряли бы одну из правок.
/// </remarks>
internal sealed class LibraryBook : ISubscriptionStore
{
    private readonly object _gate = new();

    public IReadOnlyList<SubscriptionItem> Entries()
    {
        lock (_gate)
            return SubscriptionBook.Load().Entries.Select(e => new SubscriptionItem(e.Name, e.Url, e.Working)).ToList();
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
            return SubscriptionBook.Load().Keys.ToList();
    }

    public bool KeysInPool()
    {
        lock (_gate)
            return SubscriptionBook.Load().KeysInPool;
    }

    public void Add(string name, string url, bool working)
    {
        lock (_gate)
        {
            var book = SubscriptionBook.Load();
            var found = book.Entries.FirstOrDefault(e => Same(e.Url, url));

            if (found is null)
                book.Entries.Add(new SubscriptionEntry { Name = name, Url = url.Trim(), InPool = working });
            else
            {
                found.Name = name;
                found.InPool = working || found.Working;
            }

            book.Save();

            // Указатель на первую в работе ставит сама книга — тем же кодом, что окно.
            var first = book.Pool.FirstOrDefault()?.Url;

            if (first is not null)
                SubscriptionBook.SetWorking(first, true);
        }
    }

    public void Remove(string url)
    {
        lock (_gate)
        {
            var book = SubscriptionBook.Load();
            book.Entries.RemoveAll(e => Same(e.Url, url));
            book.Save();

            // Ссылка, ушедшая из книги, не должна остаться «действующей»
            // в настройках: Load вписал бы её обратно как «Основную».
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (Same(settings.SubscriptionUrl, url))
                (settings with { SubscriptionUrl = book.Pool.FirstOrDefault()?.Url }).Save(AppSettings.DefaultPath);
        }
    }

    public void SetWorking(string url, bool working)
    {
        lock (_gate)
            SubscriptionBook.SetWorking(url, working);
    }

    public void SaveKeys(IReadOnlyList<string> keys, bool inPool)
    {
        lock (_gate)
            SubscriptionBook.SaveKeys(keys, inPool);
    }

    public string Describe(AppSettings settings)
    {
        lock (_gate)
            return SubscriptionBook.Load().Describe(settings);
    }

    public string FreeName()
    {
        lock (_gate)
            return SubscriptionBook.Load().FreeName();
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
}
