namespace NetZapret.Subscriptions;

/// <summary>Протокол прокси-сервера.</summary>
public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,

    /// <summary>
    /// WireGuard. В подписках не встречается — так подключается WARP.
    /// </summary>
    /// <remarks>
    /// Стоит особняком не названием, а местом в конфиге: sing-box собирает
    /// его не исходящим, а <c>endpoint</c>'ом, то есть сетевым интерфейсом
    /// со своим адресом. Разница видна в генераторе, а для остальной программы
    /// это такой же сервер, как прочие.
    /// </remarks>
    Wireguard,

    /// <summary>
    /// MASQUE — тот же WARP, но поверх QUIC на 443 порту.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Второй особенный: у него нет ни адреса, ни ключей, ни учётных данных.
    /// Движок в поставке несёт готовый клиент Cloudflare — при первом
    /// соединении сам идёт регистрироваться и сам хранит запись. Из настроек
    /// он принимает только через что дозваниваться, и это единственное,
    /// что мы ему даём.
    /// </para>
    /// <para>
    /// Смысл в транспорте: WireGuard узнаётся по первому же пакету, а MASQUE
    /// идёт внутри QUIC на 443 — оператору он неотличим от обычного веба.
    /// Там, где WARP по WireGuard проходит рукопожатие и глохнет на данных,
    /// у MASQUE есть шанс.
    /// </para>
    /// </remarks>
    Masque,
}

/// <summary>
/// Один сервер из подписки, приведённый к общему виду.
/// </summary>
/// <remarks>
/// Намеренно не повторяет структуру исходной ссылки: у vmess это base64 от JSON,
/// у vless — query-параметры, у ss пароль зашит в часть до <c>@</c>. Разбор
/// приводит их к одной модели, из которой генератор собирает outbound.
/// </remarks>
public sealed record ProxyServer
{
    public required ProxyProtocol Protocol { get; init; }

    /// <summary>Имя из фрагмента ссылки; используется как тег outbound'а.</summary>
    public required string Tag { get; init; }

    public required string Host { get; init; }

    public required ushort Port { get; init; }

    /// <summary>UUID для vless/vmess, пароль для trojan/ss/hysteria2.</summary>
    public required string Credential { get; init; }

    /// <summary>tcp, ws, grpc, http, httpupgrade, xhttp, quic.</summary>
    public string Transport { get; init; } = "tcp";

    /// <summary>none, tls, reality.</summary>
    public string Security { get; init; } = "none";

    public string? Sni { get; init; }

    /// <summary>Отпечаток uTLS: chrome, firefox, safari и т.п.</summary>
    public string? Fingerprint { get; init; }

    public IReadOnlyList<string> Alpn { get; init; } = Array.Empty<string>();

    /// <summary>xtls-rprx-vision для vless.</summary>
    public string? Flow { get; init; }

    /// <summary>Публичный ключ REALITY (<c>pbk</c>).</summary>
    public string? RealityPublicKey { get; init; }

    /// <summary>Короткий идентификатор REALITY (<c>sid</c>).</summary>
    public string? RealityShortId { get; init; }

    /// <summary>Путь для ws / xhttp / httpupgrade.</summary>
    public string? Path { get; init; }

    /// <summary>Заголовок Host для ws и подобных.</summary>
    public string? HostHeader { get; init; }

    /// <summary>Имя сервиса для grpc.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Метод шифрования Shadowsocks.</summary>
    public string? Method { get; init; }

    /// <summary>Альтернативный идентификатор vmess.</summary>
    public int AlterId { get; init; }

    public bool AllowInsecure { get; init; }

    /// <summary>
    /// Обфускация Hysteria2: <c>salamander</c> либо ничего.
    /// </summary>
    /// <remarks>
    /// Пропустить её нельзя. Сервер, ожидающий обфускацию, не отвечает
    /// на обычные пакеты вовсе — не ошибкой, а молчанием, — и снаружи это
    /// неотличимо от мёртвого сервера. Подписка из одиннадцати серверов,
    /// где обфускация стояла у всех, выглядела полностью нерабочей.
    /// </remarks>
    public string? ObfsType { get; init; }

    /// <summary>Пароль обфускации; отдельный от пароля подключения.</summary>
    public string? ObfsPassword { get; init; }

    /// <summary>Открытый ключ узла WireGuard.</summary>
    public string? PeerPublicKey { get; init; }

    /// <summary>
    /// Адреса нашего конца туннеля WireGuard, с маской: <c>172.16.0.2/32</c>.
    /// </summary>
    /// <remarks>
    /// У прочих протоколов такого нет вовсе: там мы клиент и своего адреса
    /// внутри не имеем. WireGuard — сеть, и без выданного адреса из неё
    /// не ответят.
    /// </remarks>
    public IReadOnlyList<string> LocalAddresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// MTU туннеля WireGuard.
    /// </summary>
    /// <remarks>
    /// 1280 — наименьший, обязательный для IPv6, и потому проходящий везде.
    /// Считать его от внешнего интерфейса можно было бы точнее, но туннель
    /// внутри туннеля: наш TUN отдаёт 1400, заголовок WireGuard съедает
    /// до восьмидесяти, и запас в сорок байт дешевле, чем разбирательство,
    /// почему грузятся мелкие страницы и не грузятся крупные.
    /// </remarks>
    public int Mtu { get; init; } = 1280;

    /// <summary>Как часто слать пустые пакеты, чтобы NAT не закрыл проход.</summary>
    public int KeepaliveSeconds { get; init; } = 30;

    /// <summary>Остальные параметры ссылки — чтобы ничего не терять при разборе.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Собирается ли этот сервер в раздел <c>endpoints</c>, а не в исходящие.
    /// </summary>
    public bool IsEndpoint => Protocol == ProxyProtocol.Wireguard;

    /// <summary>
    /// Заводит ли движок учётную запись для него сам.
    /// </summary>
    /// <remarks>
    /// Такому серверу нужен путь до регистрации, а не ключи, — и до первой
    /// удачной регистрации он не работает вовсе.
    /// </remarks>
    public bool IsSelfRegistering => Protocol == ProxyProtocol.Masque;

    /// <summary>
    /// Можно ли замерить его отдельным пробником.
    /// </summary>
    /// <remarks>
    /// Пробник поднимает свой экземпляр движка, а запись MASQUE лежит в кэше
    /// работающего, и файл кэша занят им же — bbolt держит его под замком.
    /// Значит, пробник обязан регистрироваться заново, а дозвониться до
    /// регистрации ему не через что. Отказ вышел бы не измерением, а
    /// особенностью замера, и подписать сервер «не отвечает» на этом
    /// основании — соврать.
    /// </remarks>
    public bool IsMeasurable => Protocol != ProxyProtocol.Masque;

    /// <summary>Движок, способный обслужить этот сервер.</summary>
    /// <remarks>
    /// Отсеивать больше нечего. Транспорт xhttp — придумка Xray, и до перехода
    /// на сборку extended серверы с ним приходилось пропускать: подписка
    /// из семнадцати серверов показывала четырнадцать, а целиком построенная
    /// на xhttp — ноль. Замер 2026-09-11: extended разбирает такой конфиг,
    /// обычный 1.13.19 отказывается.
    /// </remarks>
    public bool IsSupportedBySingBox => true;

    public override string ToString() =>
        $"{Protocol.ToString().ToLowerInvariant()} {Host}:{Port} [{Transport}/{Security}] {Tag}";
}
