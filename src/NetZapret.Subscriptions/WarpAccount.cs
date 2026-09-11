using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetZapret.Subscriptions;

/// <summary>
/// Учётная запись Cloudflare WARP: ключи, адреса и куда подключаться.
/// </summary>
/// <remarks>
/// <para>
/// WARP бесплатен и не требует ни почты, ни оплаты: клиент генерирует пару
/// ключей WireGuard, отдаёт открытый в Cloudflare и получает назначенные
/// адреса вместе с ключом узла. Учётная запись привязана к ключу — другой
/// ключ означает другую запись, поэтому файл с ней нужно беречь так же,
/// как ссылку подписки.
/// </para>
/// <para>
/// Записывается один раз и живёт дальше сама: перерегистрация при каждом
/// запуске плодила бы учётные записи и меняла выходной адрес, а он у WARP
/// и так непостоянен.
/// </para>
/// <para>
/// Файл лежит открытым текстом рядом с настройками — как и
/// <c>subscriptions.json</c> до него, и по той же причине: ключ шифрования
/// пришлось бы хранить тут же, а хранилище Windows привязано к пользователю
/// и не переживает переноса папки, ради которого программа и сделана
/// переносной.
/// </para>
/// </remarks>
public sealed record WarpAccount
{
    /// <summary>Закрытый ключ WireGuard, base64. Равнозначен паролю.</summary>
    public required string PrivateKey { get; init; }

    /// <summary>Открытый ключ узла Cloudflare, base64.</summary>
    public required string PeerPublicKey { get; init; }

    /// <summary>Адрес узла: обычно 162.159.192.1, адрес anycast.</summary>
    public required string EndpointHost { get; init; }

    public required int EndpointPort { get; init; }

    /// <summary>Выданный нам адрес внутри WARP.</summary>
    public required string AddressV4 { get; init; }

    public string? AddressV6 { get; init; }

    /// <summary>
    /// Идентификатор клиента, он же «reserved».
    /// </summary>
    /// <remarks>
    /// Хранится, но в конфиг не попадает: sing-box 1.14 убрал поле
    /// <c>reserved</c> у пиров WireGuard — в структуре остались только адрес,
    /// порт, ключи, allowed_ips и keepalive. Обычный <c>wg-quick</c> его тоже
    /// не знает, а через него WARP работает, так что потери нет. Сохраняем
    /// на случай, если движок вернёт поле обратно.
    /// </remarks>
    public string? ClientId { get; init; }

    /// <summary>Идентификатор записи в Cloudflare — нужен, чтобы её менять.</summary>
    public string? AccountId { get; init; }

    /// <summary>Ключ доступа к записи. Тоже равнозначен паролю.</summary>
    public string? Token { get; init; }

    /// <summary>Лицензия WARP+, если её вписали. У бесплатной записи пусто.</summary>
    public string? License { get; init; }

    public DateTimeOffset RegisteredAt { get; init; }

    public static string DefaultPath => Path.Combine("config", "warp.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Читает запись; <c>null</c> — её нет или файл испорчен.</summary>
    public static WarpAccount? Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            if (!File.Exists(target))
                return null;

            var account = JsonSerializer.Deserialize<WarpAccount>(File.ReadAllText(target), Options);

            // Запись без ключей бесполезна: конфиг с пустым private_key
            // движок не примет вовсе, и лучше считать, что записи нет.
            return string.IsNullOrWhiteSpace(account?.PrivateKey)
                || string.IsNullOrWhiteSpace(account.PeerPublicKey)
                ? null
                : account;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(target, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
    }

    /// <summary>
    /// Превращает запись в сервер, какой отдала бы подписка.
    /// </summary>
    /// <remarks>
    /// Сервер ровно один. Cloudflare раздаёт десятки портов на том же адресе,
    /// и соблазн показать их списком велик, но ключ у них общий: WireGuard
    /// различает пиров по открытому ключу, и два одновременных рукопожатия
    /// с одним ключом сервер понимает как переезд клиента — соединения
    /// начинают перебивать друг друга. Выбирать «быстрейший» там всё равно
    /// нечего: адрес anycast ведёт в ближайший узел сам.
    /// </remarks>
    public ProxyServer ToServer(string tag = DefaultTag)
    {
        var addresses = new List<string> { AddressV4 + "/32" };

        if (!string.IsNullOrWhiteSpace(AddressV6))
            addresses.Add(AddressV6 + "/128");

        return new ProxyServer
        {
            Protocol = ProxyProtocol.Wireguard,
            Tag = tag,
            Host = EndpointHost,
            Port = (ushort)EndpointPort,
            Credential = PrivateKey,
            PeerPublicKey = PeerPublicKey,
            LocalAddresses = addresses,
            Transport = "udp",
            Security = "none",
        };
    }

    /// <summary>Имя, под которым WARP показывается в списке серверов.</summary>
    public const string DefaultTag = "Cloudflare WARP";

    /// <summary>Имя второго выхода — того же WARP, но поверх QUIC.</summary>
    public const string MasqueTag = "Cloudflare WARP (MASQUE)";

    /// <summary>
    /// Второй выход WARP: MASQUE, без ключей и без учётной записи с нашей
    /// стороны.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Показывается всегда, даже когда ключей WireGuard нет: движок заводит
    /// себе запись сам, и от нас ему нужен только путь до Cloudflare. Поэтому
    /// он и не зависит от того, нажимали ли «Подключить».
    /// </para>
    /// <para>
    /// Адрес и порт здесь условны: движок выбирает узел сам и полей для них
    /// не принимает вовсе. Держим их ради общей модели — показу нужно что-то
    /// написать в подписи строки.
    /// </para>
    /// </remarks>
    public static ProxyServer MasqueServer() => new()
    {
        Protocol = ProxyProtocol.Masque,
        Tag = MasqueTag,
        Host = "cloudflareclient.com",
        Port = 443,
        Credential = string.Empty,
        Transport = "quic",
        Security = "tls",
    };
}
