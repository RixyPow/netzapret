namespace NetZapret.Core.Services;

/// <summary>
/// Часть сервиса: то, что можно направить отдельно от остального.
/// </summary>
public sealed record ServicePart
{
    /// <summary>Что это по сути: «Голос», «Видео», «Текст и картинки».</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Список Zapret, задающий состав части — например <c>lists/discord-media.txt</c>.
    /// </summary>
    /// <remarks>
    /// Ссылка на файл, а не выписанные домены. Списки ведёт проект Zapret
    /// и обновляет вместе с собой; выписанная копия устареет молча, и мы
    /// узнаем об этом, когда у кого-нибудь перестанет работать голос.
    /// </remarks>
    public required string List { get; init; }

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
/// Составлен по разбору пресета Universal V6: там части уже разделены —
/// отдельные секции для <c>discord-media.txt</c> (голос), <c>discord.txt</c>
/// (всё прочее), <c>googlevideo.txt</c> (видеопоток) и <c>youtube.txt</c>
/// (интерфейс). Мы не придумываем состав, а называем то, что там уже есть.
/// </para>
/// <para>
/// Ценность появляется там, где части расходятся: у Discord голос ломается
/// иначе, чем текст, и лечится иначе. Сервис без такого разделения смысла
/// не добавляет — его и одной строкой можно направить.
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
                    List = "lists/discord-media.txt",
                    Note = "голосовые серверы; ломается отдельно от переписки",
                },
                new ServicePart
                {
                    Name = "Текст и вход",
                    List = "lists/discord.txt",
                    Note = "переписка, шлюз, приглашения",
                },
                new ServicePart
                {
                    Name = "Картинки и вложения",
                    List = "lists/discord-images.txt",
                },
                new ServicePart
                {
                    Name = "Обновления",
                    List = "lists/discord-updates.txt",
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
                    List = "lists/googlevideo.txt",
                    Note = "сам просмотр; отдельно от страницы",
                },
                new ServicePart
                {
                    Name = "Интерфейс",
                    List = "lists/youtube.txt",
                    Note = "страница, поиск, комментарии",
                },
                new ServicePart
                {
                    Name = "Превью",
                    List = "lists/i-ytimg.txt",
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
                    Name = "Всё",
                    List = "lists/telegram.txt",
                    Note = "клиент ходит и по голым адресам — см. секцию capture",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Instagram и Facebook",
            Parts =
            [
                new ServicePart { Name = "Instagram", List = "lists/instagram.txt" },
                new ServicePart { Name = "Facebook", List = "lists/facebook.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Twitch",
            Parts = [new ServicePart { Name = "Всё", List = "lists/twitch.txt" }],
        },

        new ServiceDefinition
        {
            Name = "Roblox",
            Parts = [new ServicePart { Name = "Всё", List = "lists/roblox.txt" }],
        },

        new ServiceDefinition
        {
            Name = "Steam",
            Parts = [new ServicePart { Name = "Всё", List = "lists/steam.txt" }],
        },

        new ServiceDefinition
        {
            Name = "TikTok",
            Parts = [new ServicePart { Name = "Всё", List = "lists/tiktok.txt" }],
        },

        new ServiceDefinition
        {
            Name = "SoundCloud",
            Parts = [new ServicePart { Name = "Всё", List = "lists/soundcloud.txt" }],
        },

        new ServiceDefinition
        {
            Name = "WhatsApp",
            Parts = [new ServicePart { Name = "Всё", List = "lists/whatsapp.txt" }],
        },

        new ServiceDefinition
        {
            Name = "GitHub",
            Parts = [new ServicePart { Name = "Всё", List = "lists/github.txt" }],
        },

        new ServiceDefinition
        {
            Name = "Нейросети",
            Parts =
            [
                new ServicePart { Name = "ChatGPT", List = "lists/chatgpt.txt" },
                new ServicePart { Name = "Claude", List = "lists/claude.txt" },
                new ServicePart { Name = "Gemini", List = "lists/gemini.txt" },
                new ServicePart { Name = "DeepSeek", List = "lists/deepseek.txt" },
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
                    List = "lists/riot-valorant.txt",
                    Note = "античиты плохо переносят и десинк, и туннель",
                },
                new ServicePart { Name = "Epic Games и Fortnite", List = "lists/epicgames-fortnite.txt" },
                new ServicePart { Name = "Ubisoft", List = "lists/ubisoft.txt" },
                new ServicePart { Name = "itch.io", List = "lists/itch.txt" },
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
                    List = "lists/google.txt",
                    Note = "почта, диск, поиск — отдельно от YouTube",
                },
            ],
        },

        new ServiceDefinition
        {
            Name = "Соцсети",
            Parts =
            [
                new ServicePart { Name = "Twitter / X", List = "lists/twitter.txt" },
                new ServicePart { Name = "LinkedIn", List = "lists/linkedin.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Торренты",
            Parts =
            [
                new ServicePart { Name = "RuTracker", List = "lists/rutracker.txt" },
                new ServicePart { Name = "Rutor", List = "lists/rutor.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Работа и заметки",
            Parts =
            [
                new ServicePart { Name = "Notion", List = "lists/notion.txt" },
                new ServicePart { Name = "Obsidian", List = "lists/obsidian.txt" },
                new ServicePart { Name = "Fandom", List = "lists/fandom.txt" },
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
                    List = "lists/cloudflare.txt",
                    Note = "за ним стоит множество сайтов; трогать с осторожностью",
                },
                new ServicePart { Name = "CloudFront", List = "lists/cloudfront.txt" },
                new ServicePart { Name = "Amazon", List = "lists/amazon.txt" },
                new ServicePart { Name = "Apple", List = "lists/apple.txt" },
                new ServicePart { Name = "Microsoft Store", List = "lists/microsoft-store.txt" },
            ],
        },

        new ServiceDefinition
        {
            Name = "Speedtest",
            Parts = [new ServicePart { Name = "Всё", List = "lists/speedtest.txt" }],
        },
    ];

    public static ServiceDefinition? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
