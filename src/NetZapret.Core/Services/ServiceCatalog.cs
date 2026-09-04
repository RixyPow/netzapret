namespace NetZapret.Core.Services;

/// <summary>
/// Часть сервиса: то, что можно направить отдельно от остального.
/// </summary>
public sealed record ServicePart
{
    /// <summary>Что это по сути: «Голос», «Видео», «Текст и картинки».</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Наш список, задающий состав части.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ссылка на файл, а не выписанные домены или подсети: список можно
    /// поправить, не пересобирая программу.
    /// </para>
    /// <para>
    /// Все пути ведут в <c>config/lists/</c> — то есть в наши файлы, которые
    /// едут в архиве и обновляются вместе с программой. Прежде они вели
    /// в <c>lists/</c> установки Zapret, и это выходило боком дважды. Во-первых,
    /// правило молча переставало действовать, если установки нет: непрогруженный
    /// список не совпадает ни с чем, а в меню часть по-прежнему числилась
    /// направленной. Во-вторых, состав задавал не тот, кто отвечает за
    /// последствия: в <c>discord.txt</c> лежало два десятка чужих сайтов
    /// о Discord, в <c>roblox.txt</c> — зоны Akamai и Amazon целиком.
    /// </para>
    /// <para>
    /// Доменный (<c>config/lists/discord-media.txt</c>) либо адресный
    /// (<c>config/lists/ipset-telegram.txt</c>) — см. <see cref="ByAddress"/>.
    /// </para>
    /// </remarks>
    public required string List { get; init; }

    /// <summary>
    /// Часть задана подсетями, а не доменами.
    /// </summary>
    /// <remarks>
    /// Так устроен Telegram: у него в пресете вообще нет доменной секции —
    /// клиент ходит по адресам, не спрашивая DNS. Различать это обязательно,
    /// иначе показ врёт: спросив про домен там, где правило работает
    /// по адресу, мы получим ответ про совсем другой трафик. Именно так
    /// Telegram и показывался идущим через десинк, хотя шёл через VPN.
    /// </remarks>
    public bool ByAddress { get; init; }

    /// <summary>Пояснение, зачем эту часть трогать.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Сервис в том виде, в каком о нём думает человек.
/// </summary>
public sealed record ServiceDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyList<ServicePart> Parts { get; init; }
}

/// <summary>
/// Какие домены к какому сервису относятся.
/// </summary>
/// <remarks>
/// <para>
/// Разбиение на части взято из пресета Universal V6: там отдельные секции
/// для голоса, переписки, видеопотока и интерфейса. Ценность появляется
/// там, где части расходятся: у Discord голос ломается иначе, чем текст,
/// и лечится иначе. Сервис без такого разделения смысла не добавляет —
/// его и одной строкой можно направить.
/// </para>
/// <para>
/// А вот состав частей теперь наш. Списки лежат в <c>config/lists/</c>
/// и ведутся вместе с программой; за основу взяты списки Zapret, из которых
/// убраны три группы записей. Чужие сайты о сервисе (disboard.org, top.gg,
/// mee6.xyz у Discord) — они ломаются сами по себе, а в отчёте выглядели
/// поломкой сервиса. Целые сети доставки (akamai.net, amazonaws.com
/// у Roblox, akamaihd.net у Steam) — правило на такую зону уводит в туннель
/// половину интернета. И зеркала: тридцать восемь страновых зон Microsoft,
/// тринадцать доменов RuTracker, два десятка адресов Roblox.
/// </para>
/// <para>
/// Заодно починены две молчаливые ошибки. У Discord второй прокси картинок
/// записан как <c>mages-ext-2.discordapp.net</c> — без первой буквы, — и не
/// совпадал ни с чем. У X и GitHub картинки и raw-файлы лежат на
/// <c>twimg.com</c> и <c>githubusercontent.com</c>, которые в Zapret вынесены
/// отдельными файлами, и на них не ссылалась ни одна часть.
/// </para>
/// </remarks>
public static class ServiceCatalog
{
    public static IReadOnlyList<ServiceDefinition> All { get; } =
    [
        new ServiceDefinition
        {
            Name = "Discord",
            Parts =
            [
                new ServicePart
                {
                    Name = "Голос",
                    List = "config/lists/discord-media.txt",
                    Note = "голосовые серверы; ломается отдельно от переписки",
                },
                new ServicePart
                {
                    Name = "Текст и вход",
                    List = "config/lists/discord.txt",
                    Note = "переписка, шлюз, приглашения",
                },
                new ServicePart
                {
                    Name = "Картинки и вложения",
                    List = "config/lists/discord-images.txt",
                },
                new ServicePart
                {
                    Name = "Обновления",
                    List = "config/lists/discord-updates.txt",
                },
                new ServicePart
                {
                    Name = "Запасной путь по адресам",
                    List = "config/lists/ipset-discord.txt",
                    ByAddress = true,
                    Note = "3,4 млн адресов, среди них куски Google Cloud и Amazon — включать в крайнем случае",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "YouTube",
            Parts =
            [
                new ServicePart
                {
                    Name = "Видеопоток",
                    List = "config/lists/googlevideo.txt",
                    Note = "сам просмотр; отдельно от страницы",
                },
                new ServicePart
                {
                    Name = "Интерфейс",
                    List = "config/lists/youtube.txt",
                    Note = "страница, поиск, комментарии",
                },
                new ServicePart
                {
                    Name = "Превью",
                    List = "config/lists/i-ytimg.txt",
                },
                new ServicePart
                {
                    Name = "QUIC по адресам",
                    List = "config/lists/ipset-youtube.txt",
                    ByAddress = true,
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Telegram",
            Parts =
            [
                new ServicePart
                {
                    Name = "Приложение",
                    List = "config/lists/ipset-telegram.txt",
                    ByAddress = true,
                    Note = "клиент ходит по адресам, не спрашивая DNS — поэтому подсети, а не домены",
                },
                new ServicePart
                {
                    Name = "Сайт и веб-версия",
                    List = "config/lists/telegram.txt",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Instagram и Facebook",
            Parts =
            [
                new ServicePart { Name = "Instagram", List = "config/lists/instagram.txt" },
                new ServicePart { Name = "Facebook", List = "config/lists/facebook.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Twitch",
            Parts = [new ServicePart { Name = "Всё", List = "config/lists/twitch.txt" }],
        },

        new ServiceDefinition
        {
            Name = "Roblox",
            Parts =
            [
                new ServicePart { Name = "Игра и сайт", List = "config/lists/roblox.txt" },
                new ServicePart
                {
                    Name = "Превью предметов",
                    List = "config/lists/roblox-cdn.txt",

                    // Прежде здесь стояло «имя оборвано в DNS». Замер 2026-09-03
                    // это опроверг: Roblox перевёл имя на Akamai, цепочка
                    // разрешается до конца и адрес есть. Подстановка осталась
                    // на случай, если поломка вернётся, но постоянной быть
                    // перестала — иначе она замораживает адрес сети доставки,
                    // который живёт днями.
                    Note = "если превью не грузятся — пункт «Закрепить пин», вариант с честным резолвером",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Steam",
            Parts =
            [
                new ServicePart
                {
                    Name = "Сайт и сообщество",
                    List = "config/lists/steam-web.txt",
                    Note = "страницы; ломаются отдельно от загрузок и лечатся иначе",
                },
                new ServicePart
                {
                    Name = "Загрузки и всё прочее",
                    List = "config/lists/steam.txt",
                    Note = "игры идут десятками гигабайт — через туннель не стоит",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Valheim",
            Parts = [new ServicePart { Name = "Сайт", List = "config/lists/valheim.txt" }],
        },

        new ServiceDefinition
        {
            Name = "Кино и сериалы",
            Parts =
            [
                new ServicePart
                {
                    Name = "TMDB: постеры и описания",
                    List = "config/lists/tmdb.txt",
                    Note = "на нём держатся трекеры сериалов; без него страницы открываются без картинок",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Pinterest",
            Parts =
            [
                new ServicePart
                {
                    Name = "Сайт и картинки",
                    List = "config/lists/pinterest.txt",
                    Note = "в списках Zapret его нет, десинк к нему не применяется",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "TikTok",
            Parts = [new ServicePart { Name = "Всё", List = "config/lists/tiktok.txt" }],
        },

        new ServiceDefinition
        {
            Name = "SoundCloud",
            Parts = [new ServicePart { Name = "Всё", List = "config/lists/soundcloud.txt" }],
        },

        new ServiceDefinition
        {
            Name = "WhatsApp",
            Parts = [new ServicePart { Name = "Всё", List = "config/lists/whatsapp.txt" }],
        },

        new ServiceDefinition
        {
            Name = "GitHub",
            Parts = [new ServicePart { Name = "Всё", List = "config/lists/github.txt" }],
        },

        new ServiceDefinition
        {
            Name = "Нейросети",
            Parts =
            [
                new ServicePart { Name = "ChatGPT", List = "config/lists/chatgpt.txt" },
                new ServicePart { Name = "Claude", List = "config/lists/claude.txt" },
                new ServicePart { Name = "Gemini", List = "config/lists/gemini.txt" },
                new ServicePart { Name = "DeepSeek", List = "config/lists/deepseek.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Игры",
            Parts =
            [
                new ServicePart
                {
                    Name = "Riot и Valorant",
                    List = "config/lists/riot-valorant.txt",
                    Note = "античиты плохо переносят и десинк, и туннель",
                },
                new ServicePart { Name = "Epic Games и Fortnite", List = "config/lists/epicgames-fortnite.txt" },
                new ServicePart { Name = "Ubisoft", List = "config/lists/ubisoft.txt" },
                new ServicePart { Name = "itch.io", List = "config/lists/itch.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Google",
            Parts =
            [
                new ServicePart
                {
                    Name = "Сервисы",
                    List = "config/lists/google.txt",
                    Note = "почта, диск, поиск — отдельно от YouTube",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Соцсети",
            Parts =
            [
                new ServicePart { Name = "Twitter / X", List = "config/lists/twitter.txt" },
                new ServicePart
                {
                    Name = "Twitter / X по адресам",
                    List = "config/lists/ipset-twitter.txt",
                    ByAddress = true,
                },
                new ServicePart { Name = "LinkedIn", List = "config/lists/linkedin.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Торренты",
            Parts =
            [
                new ServicePart { Name = "RuTracker", List = "config/lists/rutracker.txt" },
                new ServicePart { Name = "Rutor", List = "config/lists/rutor.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Работа и заметки",
            Parts =
            [
                new ServicePart { Name = "Notion", List = "config/lists/notion.txt" },
                new ServicePart { Name = "Obsidian", List = "config/lists/obsidian.txt" },
                new ServicePart { Name = "Fandom", List = "config/lists/fandom.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Платформы и CDN",
            Parts =
            [
                new ServicePart
                {
                    Name = "Cloudflare",
                    List = "config/lists/cloudflare.txt",
                    Note = "за ним стоит множество сайтов; трогать с осторожностью",
                },
                new ServicePart { Name = "CloudFront", List = "config/lists/cloudfront.txt" },
                new ServicePart { Name = "Amazon", List = "config/lists/amazon.txt" },
                new ServicePart { Name = "Apple", List = "config/lists/apple.txt" },
                new ServicePart { Name = "Microsoft Store", List = "config/lists/microsoft-store.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Speedtest",
            Parts = [new ServicePart { Name = "Всё", List = "config/lists/speedtest.txt" }],
        },
    ];

    public static ServiceDefinition? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
