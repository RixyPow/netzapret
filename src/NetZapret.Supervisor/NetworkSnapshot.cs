using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetZapret.Core.Diagnostics;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>
/// Что вокруг нашей программы: чужие обходы, VPN-клиенты, адаптеры, DNS.
/// </summary>
/// <remarks>
/// <para>
/// Три отчёта подряд 28.09 упирались в «пришлите вывод команды». Обсуждение #8:
/// при остановленных движках имена разрешались в 198.18.x — подменные адреса
/// чужого клиента в режиме TUN, — а списка процессов и адаптеров в отчёте не
/// было, и доказать это было нечем. Пользователь после GoodbyeDPI: системный
/// DNS не разрешал youtube.com, а какие DNS стоят на адаптерах и не осталась ли
/// служба GoodbyeDPI, из отчёта не видно.
/// </para>
/// <para>
/// Только чтение. Адреса самого компьютера в отчёт не идут — только DNS-серверы
/// адаптеров: по ним и разбирают, а свои адреса человеку в общий чат незачем.
/// </para>
/// </remarks>
public static class NetworkSnapshot
{
    /// <summary>VPN-клиенты, которые поднимают свой TUN или прокси рядом с нашим.</summary>
    private static readonly (string Process, string Name)[] VpnClients =
    [
        ("happ", "Happ"),
        ("happd", "Happ (служба)"),
        ("xray", "Xray"),
        ("v2rayN", "v2rayN"),
        ("v2ray", "V2Ray"),
        ("hiddify", "Hiddify"),
        ("HiddifyNext", "Hiddify"),
        ("clash", "Clash"),
        ("clash-verge", "Clash Verge"),
        ("verge-mihomo", "Clash Verge (mihomo)"),
        ("mihomo", "mihomo"),
        ("nekoray", "NekoRay"),
        ("nekobox", "NekoBox"),
        ("AmneziaVPN", "AmneziaVPN"),
        ("Outline", "Outline"),
        ("wireguard", "WireGuard"),
        ("openvpn", "OpenVPN"),
        ("sing-box", "sing-box"),
    ];

    /// <summary>Имена для проверки DNS: заблокированное и обычное.</summary>
    private static readonly string[] Probes = ["youtube.com", "discord.com", "ya.ru"];

    /// <param name="ours">Номера процессов нашего надзора: свой winws2 и sing-box не чужие.</param>
    public static string Describe(IReadOnlySet<int> ours)
    {
        var text = new StringBuilder();

        Section(text, "Другие обходы (WinDivert)", () =>
        {
            var others = OtherBypassScan.Find(ours);

            return others.Count == 0
                ? ["не найдено"]
                : others.Select(o => $"{o.Name}: {o.Where}{(o.Running ? ", работает" : ", служба с автозапуском")}");
        });

        Section(text, "VPN-клиенты рядом", () => Clients(ours));

        Section(text, "Сетевые адаптеры", Adapters);

        Section(text, "DNS: системный и честный (DoH)", Resolution);

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, Func<IEnumerable<string>> lines)
    {
        text.AppendLine(title);

        try
        {
            foreach (var line in lines())
                text.AppendLine("  " + line);
        }
        catch (Exception ex)
        {
            // Отчёт собирается и тогда, когда что-то не читается: главное —
            // журналы, это дополнение к ним.
            text.AppendLine("  не прочиталось: " + ex.GetBaseException().Message);
        }

        text.AppendLine();
    }

    private static IEnumerable<string> Clients(IReadOnlySet<int> ours)
    {
        var found = new List<string>();

        foreach (var (process, name) in VpnClients)
        {
            Process[] running;

            try
            {
                running = Process.GetProcessesByName(process);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var p in running)
            {
                using (p)
                {
                    if (!ours.Contains(p.Id))
                        found.Add($"{name}: процесс {process}.exe, №{p.Id}");
                }
            }
        }

        return found.Count == 0 ? ["не найдено"] : found;
    }

    private static IEnumerable<string> Adapters()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var dns = string.Empty;

            try
            {
                var servers = nic.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToList();

                if (servers.Count > 0)
                    dns = " · DNS " + string.Join(", ", servers);
            }
            catch (NetworkInformationException)
            {
            }

            // TUN чужого клиента — то, из-за чего спорят за маршруты: Wintun,
            // TAP, «sing-tun», «SocksTunnel» у Happ.
            bool tunnel = nic.Description.Contains("tun", StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains("TAP", StringComparison.Ordinal)
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel;

            // Выключенный обычный адаптер — Wi-Fi Direct, Bluetooth — ни о чём
            // не говорит; выключенный туннель говорит о клиенте, что его завёл.
            if (nic.OperationalStatus != OperationalStatus.Up && !tunnel)
                continue;

            yield return $"{nic.Name} — {nic.Description} · {Status(nic.OperationalStatus)}"
                + (tunnel ? " · туннель" : string.Empty) + dns;
        }
    }

    private static string Status(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => "включён",
        OperationalStatus.Down => "выключен",
        _ => status.ToString(),
    };

    private static IEnumerable<string> Resolution()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var lines = new List<string>();

        foreach (var name in Probes)
        {
            var system = Wait(Task.Run(() => Dns.GetHostAddressesAsync(name)), TimeSpan.FromSeconds(5));
            var honest = Wait(DohResolver.CandidatesAsync(name, http, CancellationToken.None), TimeSpan.FromSeconds(8));

            lines.Add($"{name}: система — {Shown(system?.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()))}; "
                + $"DoH — {Shown(honest?.Take(4))}");
        }

        return lines;
    }

    /// <summary>Адреса с пометкой подмены: fakeip, петля, ноль — это не настоящий сайт.</summary>
    private static string Shown(IEnumerable<string>? addresses)
    {
        if (addresses is null)
            return "не ответил";

        var list = addresses.ToList();

        if (list.Count == 0)
            return "пусто";

        return string.Join(", ", list.Select(a =>
            IPAddress.TryParse(a, out var ip) && TunnelHealth.IsFakeIp(ip) ? a + " (подменный, TUN)"
            : a is "127.0.0.1" or "0.0.0.0" ? a + " (заглушка)"
            : a));
    }

    private static T? Wait<T>(Task<T> task, TimeSpan timeout) where T : class
    {
        try
        {
            return task.Wait(timeout) ? task.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
