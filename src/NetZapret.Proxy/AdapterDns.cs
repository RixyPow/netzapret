using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Win32;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Адаптер, которому программа может вписать DNS.</summary>
/// <param name="Index">Номер интерфейса — им адаптер называется netsh (имя бывает русским).</param>
/// <param name="StaticV4">Адреса, вписанные руками по IPv4; пусто — DNS выдаёт сеть.</param>
/// <param name="StaticV6">То же по IPv6.</param>
public sealed record DnsAdapter(int Index, string Name, IReadOnlyList<string> StaticV4, IReadOnlyList<string> StaticV6);

/// <summary>Что вышло с DNS адаптеров.</summary>
/// <param name="Changed">Адаптеры, которым DNS записан.</param>
/// <param name="Failed">Адаптеры, на которых netsh отказал.</param>
/// <param name="Refused">Почему не трогали ничего; <c>null</c> — трогали, что было нужно.</param>
public sealed record AdapterDnsResult(IReadOnlyList<string> Changed, IReadOnlyList<string> Failed, string? Refused)
{
    public static readonly AdapterDnsResult Nothing = new([], [], null);

    /// <summary>Итог словами — для окна и журнала.</summary>
    public string Describe(AdapterDnsMode mode)
    {
        if (Refused is not null)
            return Refused;

        var parts = new List<string>();

        if (Changed.Count > 0)
            parts.Add($"«{AdapterDnsModes.Word(mode)}» записано: {string.Join(", ", Changed)}");

        if (Failed.Count > 0)
            parts.Add($"не записалось: {string.Join(", ", Failed)}");

        return parts.Count == 0 ? $"«{AdapterDnsModes.Word(mode)}» уже стоит." : string.Join("; ", parts) + ".";
    }
}

/// <summary>
/// DNS адаптеров Windows по выбору человека (<see cref="AppSettings.AdapterDns"/>).
/// </summary>
/// <remarks>
/// <para>
/// С 08.11 (обсуждение №55). Пока работает туннель или DNS через движок,
/// запросы к резолверам Windows забирает движок: их адреса заводятся в TUN
/// (<see cref="SystemResolvers"/>). У владельца так и было — 8.8.8.8 вписан
/// руками, запрос к нему отвечает движок (замер 08.11). Выбор нужен, чтобы
/// человек мог сам поставить «автоматически» (роутер) или выбранный резолвер,
/// не лазая в свойства адаптера.
/// </para>
/// <para>
/// Через netsh, по номеру интерфейса: имя адаптера бывает русским, а номер
/// netsh принимает так же («имя или индекс интерфейса»). Нужны права
/// администратора — у окна они есть.
/// </para>
/// <para>
/// Трогается только то, что расходится с выбранным: адаптер, у которого уже
/// стоит нужное, — без команд. IPv6 — только если там что-то вписано руками:
/// у кого IPv6 на адаптере выключен, netsh на ipv6 отвечает ошибкой.
/// </para>
/// </remarks>
public static class AdapterDns
{
    /// <summary>Сколько ждать одну команду netsh.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Адаптеры, которым пишется DNS: подняты, Ethernet или Wi-Fi, с маршрутом
    /// по умолчанию по IPv4, не наш TUN.
    /// </summary>
    /// <remarks>
    /// Без шлюза — виртуальные сети Hyper-V и VirtualBox, у которых DNS свой
    /// по делу; чужие VPN-адаптеры (wintun, WireGuard) — другого типа.
    /// </remarks>
    public static IReadOnlyList<DnsAdapter> Targets()
    {
        var result = new List<DnsAdapter>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || !IsWired(adapter.NetworkInterfaceType)
                    || SystemResolvers.IsOwnTunnel(adapter))
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();

                if (!properties.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                {
                    continue;
                }

                result.Add(new DnsAdapter(
                    properties.GetIPv4Properties().Index,
                    adapter.Name,
                    NameServers("Tcpip", adapter.Id),
                    NameServers("Tcpip6", adapter.Id)));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // Адаптер без IPv4 или пропал посреди перебора — не наш.
            }
        }

        return result;
    }

    private static bool IsWired(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet
        or NetworkInterfaceType.Wireless80211
        or NetworkInterfaceType.GigabitEthernet
        or NetworkInterfaceType.FastEthernetT
        or NetworkInterfaceType.FastEthernetFx
        or NetworkInterfaceType.Ethernet3Megabit;

    /// <summary>Адреса, вписанные в адаптер руками (значение <c>NameServer</c>); пусто — DNS выдаёт сеть.</summary>
    /// <remarks>
    /// Из реестра: <see cref="IPInterfaceProperties.DnsAddresses"/> не говорит,
    /// откуда адрес — от сети или вписан, а «автоматически» отличается ровно этим.
    /// </remarks>
    private static IReadOnlyList<string> NameServers(string stack, string id)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{stack}\Parameters\Interfaces\{id}");

            return Split(key?.GetValue("NameServer") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>Значение <c>NameServer</c> списком: Windows пишет через запятую, старые инструменты — через пробел.</summary>
    internal static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Обычные адреса выбранного резолвера — то, что вписывается в адаптер.
    /// </summary>
    /// <param name="refusal">Почему вписать нельзя; <c>null</c> — можно.</param>
    /// <remarks>
    /// Адаптер умеет только обычный DNS на порт 53: DoH, порт dnscrypt-proxy
    /// и имя ему не задать. У Zapret DNS обычного DNS нет вовсе — только DoH
    /// за Cloudflare.
    /// </remarks>
    public static IReadOnlyList<string> ResolverAddresses(AppSettings settings, out string? refusal)
    {
        if (settings.UseRouterDns)
        {
            refusal = "Выбран DNS роутера — для адаптера это и есть «автоматически».";
            return [];
        }

        if (settings.DnsCryptActive is not null)
        {
            refusal = "Шифрованный резолвер работает на петле со своим портом — в адаптер его не вписать.";
            return [];
        }

        var provider = DnsSurvey.ByAddress(settings.DnsServer);
        var addresses = provider?.Udp
            .Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            .ToList() ?? [];

        if (addresses.Count == 0)
        {
            refusal = $"У «{provider?.Name ?? settings.DnsServer}» нет обычного DNS, только DoH — в адаптер его не вписать.";
            return [];
        }

        refusal = null;
        return addresses;
    }

    /// <summary>Стоит ли у адаптера уже то, что выбрано.</summary>
    public static bool Matches(DnsAdapter adapter, AdapterDnsMode mode, IReadOnlyList<string> addresses) => mode switch
    {
        AdapterDnsMode.Automatic => adapter.StaticV4.Count == 0 && adapter.StaticV6.Count == 0,
        AdapterDnsMode.Resolver => adapter.StaticV4.SequenceEqual(addresses, StringComparer.OrdinalIgnoreCase)
            && adapter.StaticV6.Count == 0,
        _ => true,
    };

    /// <summary>Аргументы netsh, которые приводят адаптер к выбранному.</summary>
    /// <remarks>
    /// Вписанный руками IPv6 при выбранном резолвере тоже снимается: иначе
    /// Windows спрашивала бы и его, и «выбранный резолвер» был бы им не весь.
    /// <c>validate=no</c> — без проверки netsh'ем: она ждёт ответа резолвера
    /// и при закрытом UDP тянется секундами.
    /// </remarks>
    public static IReadOnlyList<string[]> Commands(DnsAdapter adapter, AdapterDnsMode mode, IReadOnlyList<string> addresses)
    {
        var commands = new List<string[]>();
        var name = $"name={adapter.Index}";

        if (mode == AdapterDnsMode.Automatic && adapter.StaticV4.Count > 0)
            commands.Add(["interface", "ipv4", "set", "dnsservers", name, "source=dhcp"]);

        if (mode == AdapterDnsMode.Resolver && !adapter.StaticV4.SequenceEqual(addresses, StringComparer.OrdinalIgnoreCase))
        {
            commands.Add(["interface", "ipv4", "set", "dnsservers", name, "source=static",
                $"address={addresses[0]}", "register=primary", "validate=no"]);

            for (int i = 1; i < addresses.Count; i++)
                commands.Add(["interface", "ipv4", "add", "dnsservers", name, $"address={addresses[i]}", $"index={i + 1}", "validate=no"]);
        }

        if (mode != AdapterDnsMode.Keep && adapter.StaticV6.Count > 0)
            commands.Add(["interface", "ipv6", "set", "dnsservers", name, "source=dhcp"]);

        return commands;
    }

    /// <summary>
    /// Приводит DNS адаптеров к выбранному в настройках и сбрасывает кэш DNS.
    /// </summary>
    public static async Task<AdapterDnsResult> ApplyAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var mode = settings.AdapterDns;

        if (mode == AdapterDnsMode.Keep)
            return AdapterDnsResult.Nothing;

        IReadOnlyList<string> addresses = [];

        if (mode == AdapterDnsMode.Resolver)
        {
            addresses = ResolverAddresses(settings, out var refusal);

            if (refusal is not null)
                return new AdapterDnsResult([], [], refusal);
        }

        var pending = Targets().Where(a => !Matches(a, mode, addresses)).ToList();

        if (pending.Count == 0)
            return AdapterDnsResult.Nothing;

        if (!Elevated())
            return new AdapterDnsResult([], [], "Записать DNS адаптера можно только с правами администратора.");

        var changed = new List<string>();
        var failed = new List<string>();

        foreach (var adapter in pending)
        {
            bool ok = true;

            foreach (var arguments in Commands(adapter, mode, addresses))
                ok &= await NetshAsync(arguments, cancellationToken);

            (ok ? changed : failed).Add(adapter.Name);
        }

        // Кэш держит ответы прежних резолверов — без сброса выбор видно не сразу.
        if (changed.Count > 0)
            await HostsEditor.FlushDnsAsync();

        return new AdapterDnsResult(changed, failed, null);
    }

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<bool> NetshAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);

            if (process is null)
                return false;

            // Вывод читается, иначе netsh с полным буфером встал бы на записи.
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            await Task.WhenAll(output, errors);

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
