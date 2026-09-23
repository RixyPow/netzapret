using NetZapret.Core.Updates;

namespace NetZapret.Core;

/// <summary>Чужой компонент в составе поставки.</summary>
/// <param name="Name">Как он называется у себя.</param>
/// <param name="Role">Что он делает у нас — одной фразой.</param>
/// <param name="License">Лицензия, коротко.</param>
/// <param name="Source">Где лежит исходный код или страница проекта.</param>
public sealed record Component(string Name, string Role, string License, string Source);

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

    public const string Support = "https://boosty.to/rixypow/donate";

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
            "Десинк: winws2, списки и пресеты",
            "MIT",
            "https://github.com/bol-van/zapret2"),

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
