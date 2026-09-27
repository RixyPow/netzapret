using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Пароли служебных входов движка: секрет Clash API и учётка входа проверки.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 27.09 по статье вики Zapret «Критическая уязвимость VLESS-клиентов:
/// неаутентифицированный SOCKS5 на localhost». Любая программа на машине
/// сканирует порты петли за секунды и через открытый прокси узнаёт выходной
/// адрес VPN — а по нему сервер блокируют. У нас без пароля стояли вход
/// проверки <c>health-in</c> (mixed, 127.0.0.1:21090) и Clash API
/// (127.0.0.1:9090). Второй опаснее: запрос замера задержки выхода
/// с адресом на свой сервер приводит туда соединение с адреса VPN, а список
/// соединений выдаёт, куда ходит человек. Это вывод из устройства Clash API,
/// вживую не проверялся.
/// </para>
/// <para>
/// Пароли новые при каждой сборке конфига и живут только в нём. Окно, <c>nz</c>
/// и сторож читают их оттуда же, а не держат своей копии: иначе копии
/// разошлись бы с движком после первой пересборки.
/// </para>
/// <para>
/// От целевой атаки это не защищает: программа, знающая NetZapret, прочтёт
/// конфиг — в нём и адреса серверов. Закрывает это права на папку, а не пароль.
/// Пароль закрывает массовый приём — перебор портов петли с чужой методичкой.
/// </para>
/// </remarks>
public sealed record EngineKeys(string ClashSecret, string User, string Password)
{
    /// <summary>Путь к конфигу работающего движка, от корня установки.</summary>
    public static string DefaultConfigPath => Path.Combine("runtime", "singbox.json");

    /// <summary>Новые случайные пароли — для очередной сборки конфига.</summary>
    public static EngineKeys Generate() => new(Random(32), "nz-" + Random(8), Random(24));

    /// <summary>Заголовок для Clash API; <c>null</c> — секрета нет.</summary>
    public AuthenticationHeaderValue? Bearer =>
        ClashSecret.Length == 0 ? null : new AuthenticationHeaderValue("Bearer", ClashSecret);

    /// <summary>Значение <c>Proxy-Authorization</c> для HTTP CONNECT ко входу.</summary>
    public string ProxyBasic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    /// <summary>
    /// Пароли работающего движка — из его конфига; <c>null</c> — конфига нет
    /// или он собран прежней версией, без паролей.
    /// </summary>
    /// <remarks>
    /// Файл перечитывается, только когда изменился: строку «Сервер» на «Главной»
    /// окно обновляет по таймеру, и разбирать шестьдесят килобайт JSON на каждый
    /// тик незачем.
    /// </remarks>
    public static EngineKeys? Current(string? configPath = null)
    {
        var path = Path.GetFullPath(configPath ?? DefaultConfigPath);

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);

            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                    return cached.Keys;
            }

            var keys = File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

            lock (Cache)
                Cache[path] = (stamp, keys);

            return keys;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Пароли из текста конфига; <c>null</c> — их там нет.</summary>
    public static EngineKeys? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string secret = root.TryGetProperty("experimental", out var experimental)
            && experimental.TryGetProperty("clash_api", out var clash)
            && clash.TryGetProperty("secret", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;

        string user = string.Empty;
        string password = string.Empty;

        if (root.TryGetProperty("inbounds", out var inbounds))
        {
            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (inbound.TryGetProperty("tag", out var tag) && tag.GetString() == "health-in"
                    && inbound.TryGetProperty("users", out var users) && users.GetArrayLength() > 0)
                {
                    user = users[0].GetProperty("username").GetString() ?? string.Empty;
                    password = users[0].GetProperty("password").GetString() ?? string.Empty;
                }
            }
        }

        return secret.Length == 0 && user.Length == 0 ? null : new EngineKeys(secret, user, password);
    }

    /// <summary>Подписывает запрос к Clash API, если секрет известен.</summary>
    public static void Authorize(HttpRequestMessage request, EngineKeys? keys)
    {
        if (keys?.Bearer is { } bearer)
            request.Headers.Authorization = bearer;
    }

    private static string Random(int length)
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];

        for (int i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];

        return new string(chars);
    }

    private static readonly Dictionary<string, (DateTime Stamp, EngineKeys? Keys)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
}
