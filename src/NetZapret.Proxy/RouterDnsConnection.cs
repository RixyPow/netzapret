using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetZapret.Core;
namespace NetZapret.Proxy;

/// <summary>Первый адаптер со шлюзом может оказаться чужим: выбираем LAN по подсети DNS.</summary>
public static class RouterDnsConnection
{
    public sealed record Lan(string Name, int Index, IPAddress LocalAddress, int PrefixLength);

    public sealed record Selection(AppSettings Settings, Lan? Interface, string? Warning);

    public static Selection Resolve(AppSettings requested, Func<string, Lan>? find = null)
    {
        if (!requested.UseRouterDns) return new(requested, null, null);
        try { return new(requested, (find ?? Find)(requested.RouterDnsAddress), null); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or NetworkInformationException or SocketException or PlatformNotSupportedException)
        {
            return new(requested with { UseRouterDns = false, DnsAuto = false, DnsVia = DnsRoute.Direct }, null,
                $"ВНИМАНИЕ: DNS роутера {requested.RouterDnsAddress} недоступен в этой LAN. "
                + $"Используется запасной DoH {requested.DnsServer}, напрямую. PBR роутера может не получить DNS-запросы. "
                + "После подключения к нужной сети перезапустите движки для повторной попытки.");
        }
    }

    /// <summary>Читаем сохранённый результат сборки, не сам конфиг; маленькое состояние кэшируется.</summary>
    public static string? ConfigWarning(AppSettings requested)
    {
        if (!requested.UseRouterDns) return null;
        var state = RouterDnsBuildState.Load(requested.ProxyConfigPath);
        return state?.RequestedRouterAddress == requested.RouterDnsAddress ? state.Warning : null;
    }

    public static bool SameSubnet(IPAddress local, IPAddress remote, IPAddress mask)
    {
        var a = local.GetAddressBytes(); var b = remote.GetAddressBytes(); var m = mask.GetAddressBytes();
        return a.Length == 4 && b.Length == 4 && m.Length == 4
            && Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (b[i] & m[i]));
    }

    public static Lan Find(string address)
    {
        if (!RouterDnsSettings.TryNormalize(address, out var normalized))
            throw new ArgumentException("Неверный IPv4 DNS роутера.");
        var target = IPAddress.Parse(normalized);
        var candidates = new List<Lan>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                || new[] { "netzapret", "wintun", "sing-tun", "wireguard", "tap-windows", "vpn" }
                    .Any(m => (nic.Name + " " + nic.Description).Contains(m, StringComparison.OrdinalIgnoreCase)))
                continue;
            var properties = nic.GetIPProperties();
            foreach (var local in properties.UnicastAddresses)
            {
                if (local.Address.AddressFamily != AddressFamily.InterNetwork || local.IPv4Mask is null
                    || !SameSubnet(local.Address, target, local.IPv4Mask)) continue;
                if (local.Address.Equals(target))
                    throw new ArgumentException("Указан IP этого компьютера, а не DNS роутера.");
                if (properties.GetIPv4Properties() is { } v4)
                    candidates.Add(new(nic.Name, v4.Index, local.Address, local.PrefixLength));
            }
        }
        return candidates.OrderByDescending(c => c.PrefixLength).ThenBy(c => c.Index).FirstOrDefault()
            ?? throw new InvalidOperationException("Не найден активный Ethernet/Wi-Fi адаптер в подсети DNS роутера. Проверьте IP, подключение и маску сети.");
    }

    public static bool IsAnswer(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response) =>
        query.Length >= 2 && response.Length >= 12 && query[0] == response[0] && query[1] == response[1]
        && (response[2] & 0x80) != 0;

    /// <summary>
    /// Годится ли роутер в DNS: отвечает ли он и не подменяет ли закрытые имена.
    /// </summary>
    /// <remarks>
    /// Ответа на example.com мало. 15.10 у владельца роутер на OpenWRT отвечал
    /// на него честно, а на rutracker, nnmclub, linkedin, instagram, twitter
    /// и proton — одной заглушкой 81.200.2.238, которая соединений не принимает:
    /// сам роутер спрашивал 1.1.1.1 открытым UDP, и ответы по дороге подменяли.
    /// В «Гибриде» с таким DNS twitter.com и facebook.com, которые десинк
    /// открывает по настоящему адресу (301 и 200), не открывались вовсе.
    /// Поэтому роутер спрашивается о тех же закрытых именах, что и резолверы
    /// в обзоре (<see cref="DnsSurvey.SpoofProbes"/>), и судит тот же
    /// <see cref="DnsSurvey.IsSpoofed"/>.
    /// </remarks>
    public static async Task<RouterDnsCheck> CheckAsync(string address, CancellationToken ct)
    {
        Lan lan;
        try { lan = Find(address); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NetworkInformationException)
        { return new(RouterDnsFit.Silent, ex.Message); }

        var probe = await AskAsync(lan, address, "example.com", ct);
        if (probe is null)
        {
            return new(RouterDnsFit.Silent,
                $"Роутер {address} не ответил по UDP/53 за 4 секунды через «{lan.Name}». Проверьте адрес и что DNS на роутере открыт для локальной сети.");
        }

        // Честные ответы и ответы роутера — разом: по очереди это десяток
        // запросов по 4 секунды в худшем случае.
        var honest = DnsSurvey.HonestAnswersAsync(lan.Index, ct);
        var asked = DnsSurvey.SpoofProbes.Select(async name => (name, answer: await AskAsync(lan, address, name, ct))).ToArray();
        await Task.WhenAll(asked);

        var router = new Dictionary<string, IReadOnlyList<IPAddress>>();
        foreach (var (name, answer) in asked.Select(t => t.Result))
        {
            if (answer is not null)
                router[name] = answer.Addresses;
        }

        return Judge(lan.Name, router, await honest);
    }

    /// <summary>Приговор по ответам роутера и честного DoH на закрытые имена.</summary>
    /// <param name="router">Что ответил роутер; имени нет — роутер промолчал.</param>
    public static RouterDnsCheck Judge(
        string lanName,
        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> router,
        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> honest)
    {
        // Заглушка оператора одна на все имена — тот же признак, что в обзоре.
        var shared = router.Values
            .SelectMany(a => a.Distinct())
            .GroupBy(a => a)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var compared = router
            .Where(r => honest.TryGetValue(r.Key, out var truth) && truth.Count > 0)
            .ToList();

        var spoofed = compared
            .Where(r => DnsSurvey.IsSpoofed(r.Value, honest[r.Key], shared))
            .ToList();

        if (spoofed.Count > 0)
        {
            var (name, answer) = (spoofed[0].Key, spoofed[0].Value);
            var said = answer.Count == 0 ? "пустой ответ" : string.Join(", ", answer);

            return new(RouterDnsFit.Spoofs,
                $"Роутер подменяет ответы: {spoofed.Count} из {compared.Count} заблокированных имён получили не настоящий адрес "
                + $"(например, {name} → {said}). С таким DNS перестанут открываться сайты под десинком и напрямую. "
                + "Подойдёт роутер, который сам спрашивает DNS по DoH или DoT (https-dns-proxy, stubby).");
        }

        if (compared.Count == 0)
        {
            return new(RouterDnsFit.Unknown,
                $"Роутер отвечает через «{lanName}», но сверить его ответы не с чем: честный DoH не ответил. "
                + "Подменяет ли он заблокированные имена, неизвестно. Если сайты под десинком перестанут открываться, выключите DNS роутера.");
        }

        return new(RouterDnsFit.Fits,
            $"Роутер подходит: отвечает через «{lanName}», заблокированные имена — настоящими адресами ({compared.Count} из {compared.Count}). "
            + "Это проверка DNS, а не маршрутизации и VPN на самом роутере.");
    }

    /// <summary>Один запрос к роутеру по UDP через его адаптер; <c>null</c> — не ответил.</summary>
    private static async Task<DnsWire.Answer?> AskAsync(Lan lan, string address, string name, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(lan.Index));
        socket.Bind(new IPEndPoint(lan.LocalAddress, 0));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), 53), limit.Token);
            var query = DnsWire.Query(name, DnsWire.TypeA);
            await socket.SendAsync(query, SocketFlags.None, limit.Token);
            var buffer = new byte[4096];
            int size = await socket.ReceiveAsync(buffer, SocketFlags.None, limit.Token);
            return IsAnswer(query, buffer.AsSpan(0, size)) ? DnsWire.Parse(buffer.AsSpan(0, size)) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
    }
}

/// <summary>Что показала проверка роутера.</summary>
public enum RouterDnsFit
{
    /// <summary>Отвечает, закрытые имена — настоящими адресами.</summary>
    Fits,

    /// <summary>Отвечает, но подменяет закрытые имена.</summary>
    Spoofs,

    /// <summary>Не отвечает или не найден в этой сети.</summary>
    Silent,

    /// <summary>Отвечает, а сверить не с чем.</summary>
    Unknown,
}

/// <summary>Приговор проверки роутера и его объяснение для человека.</summary>
public sealed record RouterDnsCheck(RouterDnsFit Fit, string Text)
{
    /// <summary>Можно ли включать: подмена и молчание закрыли бы сайты.</summary>
    public bool Allows => Fit is RouterDnsFit.Fits or RouterDnsFit.Unknown;
}
