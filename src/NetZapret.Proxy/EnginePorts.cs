using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>Служебные порты движка из его конфига: Clash API и вход проверки <c>health-in</c>.</summary>
public sealed record EnginePortSet(int ClashPort, int HealthPort);

/// <summary>
/// Подбор и чтение служебных портов <c>sing-box</c> (<c>clash_api</c> и <c>health-in</c>).
/// </summary>
/// <remarks>
/// <para>
/// Issue #85 (23.11): при включённом Hyper-V, WSL2 или Docker служба Windows
/// <c>winnat</c> резервирует случайные диапазоны TCP-портов. Когда порт
/// <c>21090</c> (<c>health-in</c>) или <c>9090</c> (<c>clash_api</c>) попадает
/// в такой диапазон, <c>sing-box</c> падает при запуске с
/// <c>WSAEACCES</c> («An attempt was made to access a socket in a way forbidden
/// by its access permissions»).
/// </para>
/// <para>
/// Перед сборкой конфига порт проверяется пробной привязкой на петле; если он
/// запрещён системой или занят чужим процессом, берётся свободный порт от ОС.
/// Надзор, окно и <c>nz</c> читают фактические порты из <c>runtime\singbox.json</c>
/// (с кэшем по времени записи файла, как <see cref="EngineKeys.Current"/>).
/// </para>
/// </remarks>
public static class EnginePorts
{
    private static readonly EnginePortSet Defaults =
        new(TunnelStatus.DefaultClashPort, SingBoxOptions.DefaultHealthPort);

    /// <summary>Фактические служебные порты из конфига; если файла нет — порты по умолчанию.</summary>
    public static EnginePortSet Current(string? configPath = null)
    {
        var path = Path.GetFullPath(configPath ?? EngineKeys.DefaultConfigPath);

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);

            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                    return cached.Ports;
            }

            var ports = File.Exists(path) ? Parse(File.ReadAllText(path)) : Defaults;

            lock (Cache)
                Cache[path] = (stamp, ports);

            return ports;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Defaults;
        }
    }

    public static int CurrentClashPort(string? configPath = null) => Current(configPath).ClashPort;

    public static string CurrentClashListen(string? configPath = null) => $"127.0.0.1:{CurrentClashPort(configPath)}";

    public static int CurrentHealthPort(string? configPath = null) => Current(configPath).HealthPort;

    /// <summary>Читает порты <c>clash_api</c> и <c>health-in</c> из текста <c>singbox.json</c>.</summary>
    public static EnginePortSet Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        int clashPort = Defaults.ClashPort;
        int healthPort = Defaults.HealthPort;

        if (root.TryGetProperty("experimental", out var experimental)
            && experimental.TryGetProperty("clash_api", out var clash)
            && clash.TryGetProperty("external_controller", out var controller)
            && controller.GetString() is { Length: > 0 } listen)
        {
            int colon = listen.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(listen[(colon + 1)..], out int parsed) && parsed is > 0 and <= 65535)
                clashPort = parsed;
        }

        if (root.TryGetProperty("inbounds", out var inbounds))
        {
            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (inbound.TryGetProperty("tag", out var tag) && tag.GetString() == "health-in"
                    && inbound.TryGetProperty("listen_port", out var portEl) && portEl.TryGetInt32(out int parsed)
                    && parsed is > 0 and <= 65535)
                {
                    healthPort = parsed;
                    break;
                }
            }
        }

        return new EnginePortSet(clashPort, healthPort);
    }

    /// <summary>
    /// Выбирает пригодный порт на <c>127.0.0.1</c>: сначала пробует <paramref name="preferred"/>,
    /// а если он запрещён системой (резерв Hyper-V / WinNAT, <c>WSAEACCES</c>) или занят чужой
    /// программой, берёт свободный порт у ОС.
    /// </summary>
    /// <remarks>
    /// При пересборке конфига до перезапуска наш же <c>sing-box</c> ещё держит свой порт
    /// (<see cref="SocketError.AddressAlreadyInUse"/>). Если этот порт записан в текущем
    /// конфиге, оставляем его — старый движок отпустит порт перед стартом нового.
    /// А при <see cref="SocketError.AccessDenied"/> (Hyper-V / WinNAT) всегда уходим на другой.
    /// </remarks>
    public static int Pick(
        int preferred,
        string? configPath = null,
        IReadOnlyCollection<int>? avoid = null,
        Func<int, SocketError?>? probe = null,
        Func<int>? allocate = null)
    {
        var check = probe ?? ProbeLoopback;
        var blocked = avoid is { Count: > 0 } ? new HashSet<int>(avoid) : null;

        if (blocked is null || !blocked.Contains(preferred))
        {
            var error = check(preferred);

            if (error is null)
                return preferred;

            if (error == SocketError.AddressAlreadyInUse && HeldByConfig(preferred, configPath))
                return preferred;
        }

        var next = allocate ?? AllocateLoopbackPort;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            int candidate = next();
            if (candidate is > 0 and <= 65535
                && (blocked is null || !blocked.Contains(candidate))
                && (probe is null || check(candidate) is null))
            {
                return candidate;
            }
        }

        return preferred;
    }

    private static bool HeldByConfig(int port, string? configPath)
    {
        var path = Path.GetFullPath(configPath ?? EngineKeys.DefaultConfigPath);
        if (!File.Exists(path))
            return false;

        var current = Current(path);
        return current.ClashPort == port || current.HealthPort == port;
    }

    private static SocketError? ProbeLoopback(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }

    private static int AllocateLoopbackPort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
            return 0;
        }
    }

    private static readonly Dictionary<string, (DateTime Stamp, EnginePortSet Ports)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
}
