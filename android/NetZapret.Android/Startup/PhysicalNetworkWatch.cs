using System.Collections.Concurrent;
using Android.Content;
using Android.Net;

namespace NetZapret.Android.Startup;

/// <summary>
/// Какая сеть телефона физическая (Wi-Fi, мобильная, кабель) — для
/// <c>PhysicalNetwork.InterfaceSource</c> библиотек.
/// </summary>
/// <remarks>
/// <para>
/// Windows-путь читает шлюзы адаптеров, а на Android .NET на
/// <c>GatewayAddresses</c> бросает PlatformNotSupportedException
/// (portability.md S3): /proc/net/route приложению закрыт. Без этого крючка
/// надзор, панели подписок, лестница DNS и проверка блокировок падали бы
/// на первом же вопросе «есть ли сеть», а перехват исключения превратил бы
/// ответ в вечное «нет сети».
/// </para>
/// <para>
/// Знает сеть ConnectivityManager. Подписка — на сети с интернетом и без VPN
/// (NOT_VPN): наш же туннель и чужой VPN физической сетью не считаются.
/// </para>
/// </remarks>
internal sealed class PhysicalNetworkWatch : ConnectivityManager.NetworkCallback
{
    private static PhysicalNetworkWatch? _instance;
    private static ConnectivityManager? _connectivity;

    private readonly ConcurrentDictionary<long, Entry> _networks = new();

    /// <summary>Посчитанный ответ; null — пересчитать при следующем вопросе.</summary>
    private volatile Answer? _answer;

    private sealed record Entry(Network Network, string? Interface, bool Wired, bool Wifi, bool Cellular);

    private sealed record Answer(int? Index, Network? Network);

    public static void Start(Context context)
    {
        if (_instance is not null)
            return;

        _connectivity = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);

        if (_connectivity is null)
            return;

        var watch = new PhysicalNetworkWatch();
        watch.Seed();

        var request = new NetworkRequest.Builder()
            .AddCapability(NetCapability.Internet)!
            .AddCapability(NetCapability.NotVpn)!
            .Build()!;

        _connectivity.RegisterNetworkCallback(request, watch);
        _instance = watch;
    }

    /// <summary>Индекс интерфейса физической сети; null — сети нет.</summary>
    public static int? InterfaceIndex() => _instance?.Current().Index;

    /// <summary>Сама физическая сеть — для частного DNS и привязки сокетов.</summary>
    public static Network? CurrentNetwork() => _instance?.Current().Network;

    /// <summary>Свойства физической сети (частный DNS, резолверы); null — сети нет.</summary>
    public static LinkProperties? CurrentLinkProperties() =>
        CurrentNetwork() is { } network ? _connectivity?.GetLinkProperties(network) : null;

    /// <summary>Есть ли сейчас VPN-сети — свои и чужие.</summary>
    public static int VpnNetworkCount()
    {
        if (_connectivity is null)
            return 0;

        int count = 0;

#pragma warning disable CA1422 // GetAllNetworks устарел в API 31, но замены «перечислить все» нет.
        foreach (var network in _connectivity.GetAllNetworks() ?? [])
        {
            if (_connectivity.GetNetworkCapabilities(network)?.HasTransport(TransportType.Vpn) == true)
                count++;
        }
#pragma warning restore CA1422

        return count;
    }

    public override void OnAvailable(Network network)
    {
        Update(network);
    }

    public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities)
    {
        Update(network, networkCapabilities);
    }

    public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties)
    {
        Update(network, linkProperties: linkProperties);
    }

    public override void OnLost(Network network)
    {
        _networks.TryRemove(network.NetworkHandle, out _);
        _answer = null;
    }

    /// <summary>
    /// Сети, что уже есть: обратный вызов о них придёт не сразу, а надзор
    /// может спросить в первые же миллисекунды после запуска.
    /// </summary>
    private void Seed()
    {
#pragma warning disable CA1422
        foreach (var network in _connectivity!.GetAllNetworks() ?? [])
            Update(network);
#pragma warning restore CA1422
    }

    private void Update(Network network, NetworkCapabilities? capabilities = null, LinkProperties? linkProperties = null)
    {
        try
        {
            capabilities ??= _connectivity!.GetNetworkCapabilities(network);

            if (capabilities is null
                || capabilities.HasTransport(TransportType.Vpn)
                || !capabilities.HasCapability(NetCapability.Internet))
            {
                _networks.TryRemove(network.NetworkHandle, out _);
                _answer = null;
                return;
            }

            linkProperties ??= _connectivity!.GetLinkProperties(network);

            _networks[network.NetworkHandle] = new Entry(
                network,
                linkProperties?.InterfaceName,
                capabilities.HasTransport(TransportType.Ethernet),
                capabilities.HasTransport(TransportType.Wifi),
                capabilities.HasTransport(TransportType.Cellular));
        }
        catch (Java.Lang.Exception)
        {
            // Сеть пропала между уведомлением и вопросом — придёт OnLost.
        }

        _answer = null;
    }

    private Answer Current()
    {
        if (_answer is { } known)
            return known;

        var picked = Pick();
        var answer = new Answer(picked is null ? null : IndexOf(picked.Interface), picked?.Network);

        // Сеть без имени интерфейса (бывает в первые мгновения) — не сеть:
        // сокет к ней не привязать, и ответ не запоминается.
        if (picked is null || answer.Index is not null)
            _answer = answer;

        return answer;
    }

    /// <summary>
    /// Сеть по умолчанию для нас, если она физическая; иначе кабель, Wi-Fi, мобильная.
    /// </summary>
    /// <remarks>
    /// Наше приложение исключено из своего VPN, и ActiveNetwork для него —
    /// та сеть, по которой пойдут прямые соединения. Когда Wi-Fi и мобильная
    /// подняты обе, это отличает настоящую от запасной.
    /// </remarks>
    private Entry? Pick()
    {
        var all = _networks.Values.Where(e => !string.IsNullOrEmpty(e.Interface)).ToList();

        if (all.Count == 0)
            return null;

        try
        {
            if (_connectivity?.ActiveNetwork is { } active
                && all.FirstOrDefault(e => e.Network.NetworkHandle == active.NetworkHandle) is { } preferred)
            {
                return preferred;
            }
        }
        catch (Java.Lang.Exception)
        {
        }

        return all.FirstOrDefault(e => e.Wired)
            ?? all.FirstOrDefault(e => e.Wifi)
            ?? all.FirstOrDefault(e => e.Cellular)
            ?? all[0];
    }

    /// <summary>Индекс интерфейса ядра — тот же, что .NET отдаёт в GetIPv4Properties().Index.</summary>
    private static int? IndexOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        try
        {
            if (Java.Net.NetworkInterface.GetByName(name) is { } nic && nic.Index > 0)
                return nic.Index;
        }
        catch (Java.Lang.Exception)
        {
        }

        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));

            return nic?.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
