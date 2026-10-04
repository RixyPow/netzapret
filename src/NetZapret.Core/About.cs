using NetZapret.Core.Updates;

namespace NetZapret.Core;

/// <summary>Чужой компонент в составе поставки.</summary>
/// <param name="Name">Как он называется у себя.</param>
/// <param name="Role">Что он делает у нас — одной фразой.</param>
/// <param name="License">Лицензия, коротко.</param>
/// <param name="Source">Где лежит исходный код или страница проекта.</param>
/// <param name="Site">Сайт проекта, если он отдельно от исходников; <c>null</c> — нет.</param>
public sealed record Component(string Name, string Role, string License, string Source, string? Site = null);

/// <summary>
/// Кто делает программу и из чего она собрана — для карточки «О программе».
/// </summary>
/// <remarks>
/// Просьба из обсуждения #2. Разбор лицензий лежит в <c>docs/THIRD-PARTY.md</c>,
/// но пользователь архива этого файла не видит, а для sing-box и WinDivert
/// указать, где взять исходники, — условие их лицензий. Список держим здесь,
/// а не в разметке окна: сверку с <c>THIRD-PARTY.md</c> делает тест, и она
/// не должна зависеть от окна.
/// </remarks>
public static class About
{
    public const string Author = "RixyPow";

    public const string Repository = "https://github.com/" + UpdateCheck.Repository;

    public const string Issues = Repository + "/issues";

    public const string Discussions = Repository + "/discussions";

    public const string Telegram = "https://t.me/netzapret23";

    public const string Support = "https://www.donationalerts.com/r/netzapret";

    /// <summary>
    /// Состав поставки, в порядке важности для работы.
    /// </summary>
    /// <remarks>
    /// Ссылки — на исходники именно той сборки, что едет в архиве. У sing-box
    /// это форк extended, а не основной проект: GPL требует назвать источник
    /// изменённой сборки.
    /// </remarks>
    public static IReadOnlyList<Component> Components { get; } =
    [
        new("sing-box extended",
            "Туннель: VPN-серверы подписки и WARP",
            "GPL v3",
            "https://github.com/shtorm-7/sing-box-extended"),

        new("Zapret 2",
            "Десинк: winws2 и его библиотека lua",
            "MIT",
            "https://github.com/bol-van/zapret2"),

        // До 30.09 всё, что едет рядом с winws2, было приписано bol-van.
        // Сценарии lua сверх шести его модулей, списки, пресеты и каталог
        // адресов — из Zapret GUI; автора назвал владелец, лицензия и подпись
        // коммитов сверены по репозиторию (docs/THIRD-PARTY.md).
        new("Zapret GUI",
            "Сценарии десинка на lua, списки доменов и адресов, пресеты, каталог адресов — автор loop-uh",
            "MIT",
            "https://git.zapret.moe/zapretdiscordyoutube/zapretgui",
            "https://wiki.zapret.moe/"),

        // Game filter — перенос его выключателя и секций ALT11 (GameFilter);
        // список ipset-all и три образца пакетов сверены с его репозиторием
        // 30.09 — совпадают целиком.
        new("zapret-discord-youtube",
            "Game filter: секции игр, список адресов ipset-all и образцы пакетов к ним — автор Flowseal",
            "MIT",
            "https://github.com/Flowseal/zapret-discord-youtube"),

        new("WinDivert",
            "Перехват пакетов для winws2",
            "LGPL v3 / GPL v3",
            "https://github.com/basil00/WinDivert"),

        new("Wintun",
            "Сетевой адаптер туннеля",
            "Проприетарная, WireGuard LLC",
            "https://www.wintun.net/"),

        new("Cygwin",
            "Среда, в которой работает winws2",
            "LGPL v3",
            "https://cygwin.com/"),
    ];
}
