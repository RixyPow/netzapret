namespace NetZapret.Core.Connections;

/// <summary>
/// Источник событий о новых соединениях.
/// </summary>
/// <remarks>
/// Главный шов архитектуры. PoC получает события постфактум из WFP net-events
/// (решение уже принято системой, мы только наблюдаем). Если позже появится
/// собственный callout-драйвер, он реализует этот же интерфейс, но синхронно —
/// и rule engine переезжает без изменений.
/// </remarks>
public interface IConnectionEventSource : IAsyncDisposable
{
    /// <summary>Человекочитаемое имя источника для логов.</summary>
    string Name { get; }

    /// <summary>
    /// Поток событий. Завершается при отмене токена или при остановке источника.
    /// </summary>
    IAsyncEnumerable<ConnectionEvent> ReadEventsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Поставщик доменных имён для адреса назначения.
/// </summary>
/// <remarks>
/// В PoC — статическая карта из файла. Далее её место займёт fakeip-пул
/// sing-box, где соответствие «адрес → домен» точное 1:1, в отличие от
/// наблюдения за реальными ответами DNS, где один адрес CDN раздаётся сотням имён.
/// </remarks>
public interface IHostnameSource
{
    /// <summary>Возвращает имя для адреса или <c>null</c>, если оно неизвестно.</summary>
    string? Resolve(System.Net.IPAddress address);
}

/// <summary>Пустой поставщик: имён нет, доменные правила не срабатывают.</summary>
public sealed class NullHostnameSource : IHostnameSource
{
    public static readonly NullHostnameSource Instance = new();

    private NullHostnameSource() { }

    public string? Resolve(System.Net.IPAddress address) => null;
}
