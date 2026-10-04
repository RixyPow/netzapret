namespace NetZapret.Core.Rules;

/// <summary>
/// Режим работы — то, что человек выбирает на «Главной».
/// </summary>
/// <remarks>
/// <para>
/// Владелец 04.10: вместо двух выключателей — три режима: «Десинк»,
/// «Туннель» и общий — «Гибрид» (сперва звался «NZ Route», в тот же день переименован). Осмысленных сочетаний у выключателей
/// и было три, но человек видел два независимых рычага и не видел,
/// что вместе они дают третий режим — тот, ради которого программа
/// написана (docs/ideas.md, №12).
/// </para>
/// <para>
/// Хранится по-прежнему выключателями (<see cref="EngineChoice"/>): из них
/// режим выводится однозначно, и переносить настройки при обновлении
/// не нужно. «Ничего не поднято» режимом не считается — это остановка,
/// а не выбор.
/// </para>
/// </remarks>
public enum WorkMode
{
    /// <summary>Только десинк: VPN не поднимается, адрес домашний.</summary>
    Desync,

    /// <summary>Только туннель: всё через VPN, кроме поставленного «напрямую».</summary>
    Tunnel,

    /// <summary>Оба движка: куда что идёт, решают маршруты.</summary>
    Route,
}

/// <summary>Названия, пояснения и перевод режимов в выключатели и обратно.</summary>
public static class WorkModes
{
    /// <summary>Название общего режима. Одно место — чтобы переименовать одной строкой.</summary>
    public const string RouteName = "Гибрид";

    public static IReadOnlyList<WorkMode> All { get; } = [WorkMode.Desync, WorkMode.Tunnel, WorkMode.Route];

    public static string Name(WorkMode mode) => mode switch
    {
        WorkMode.Desync => "Десинк",
        WorkMode.Tunnel => "Туннель",
        _ => RouteName,
    };

    /// <summary>Что режим делает с трафиком — одной фразой для выбора.</summary>
    public static string Explain(WorkMode mode) => mode switch
    {
        WorkMode.Desync => "Чинит блокировки по имени. Трафик напрямую, адрес домашний.",
        WorkMode.Tunnel => "Весь трафик через VPN, кроме поставленного «напрямую».",
        _ => "Что через VPN, а что напрямую с десинком, — решают маршруты.",
    };

    /// <summary>Какой режим сейчас; <c>null</c> — ничего не поднято.</summary>
    public static WorkMode? Of(EngineChoice choice) => (choice.Desync, choice.Tunnel) switch
    {
        (true, false) => WorkMode.Desync,
        (false, true) => WorkMode.Tunnel,
        (true, true) => WorkMode.Route,
        _ => null,
    };

    /// <summary>Выключатели под режим; «без исключений» и прочее — как было.</summary>
    public static EngineChoice Apply(EngineChoice choice, WorkMode mode) => mode switch
    {
        WorkMode.Desync => choice with { Desync = true, Tunnel = false },
        WorkMode.Tunnel => choice with { Desync = false, Tunnel = true },
        _ => choice with { Desync = true, Tunnel = true },
    };
}
