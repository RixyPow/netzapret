using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace NetZapret.Proxy;

/// <summary>
/// Адреса серверов туннеля из его конфига — чтобы вывести их из перехвата десинка.
/// </summary>
/// <remarks>
/// <para>
/// Замер владельца 30.09, A/B/A через вход проверки туннеля (Эстония, VLESS
/// на 443): с поднятым десинком скачивание 14,6 / 15,2 / 15,4 Мбит/с, отдача
/// 1,3 / 1,4 / 1,3, задержка 0,27 / 0,25 / 0,27 с; без десинка — 149,3
/// и 24,1 Мбит/с, 0,12 с. winws2 при этом занимал 88–98 % одного ядра,
/// а он однопоточный. Пресет перехватывает весь исходящий TCP на 443–65535,
/// и соединение самого туннеля с сервером шло через winws2 пакет за пакетом.
/// Десинку в нём делать нечего: ни адреса серверов, ни их имена не лежат
/// ни в одном его списке (проверено в тот же день).
/// </para>
/// <para>
/// Имя сервера вместо адреса разрешается здесь же, всё разом и с общим
/// коротким сроком: адрес нужен до запуска winws2, а зависший резолвер
/// не должен держать запуск десинка. Не разрешилось — сервер остаётся
/// в перехвате, как было до 30.09.
/// </para>
/// <para>
/// Не проверено: сервер за CDN. Его имя разрешается в общий адрес CDN, и вместе
/// с туннелем из перехвата уйдут чужие сайты на том же адресе. У владельца все
/// серверы заданы адресами, и проверить это было не на чем.
/// </para>
/// </remarks>
public static class TunnelEndpoints
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Адреса серверов из конфига sing-box; не прочитался — пусто.</summary>
    /// <param name="first">
    /// Тег выхода, чей адрес идёт первым, — закреплённый сервер. В фильтр
    /// перехвата помещаются не все адреса, и закреплённый не должен остаться за чертой.
    /// </param>
    public static IReadOnlyList<IPAddress> Read(string configPath, string? first = null)
    {
        JsonNode? config;

        try
        {
            config = JsonNode.Parse(File.ReadAllText(configPath));
        }
        catch (Exception)
        {
            return [];
        }

        // Порядок серверов сохраняется: готовый адрес — уже решённая задача.
        var lookups = Hosts(config, first)
            .Select(host => IPAddress.TryParse(host, out var address)
                ? Task.FromResult(new[] { address })
                : Dns.GetHostAddressesAsync(host))
            .ToList();

        try
        {
            Task.WaitAll([.. lookups], ResolveTimeout);
        }
        catch (Exception)
        {
            // Не разрешилось — этот сервер десинк по-прежнему видит, и только.
        }

        var result = lookups
            .Where(l => l.IsCompletedSuccessfully)
            .SelectMany(l => l.Result);

        return result
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Диапазон подменных адресов IPv4 из конфига движка; нет его — <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Подменные адреса раздаются только при выборочном перехвате. При полном
    /// их нет, и в туннель идёт всё по настоящим адресам — такое из перехвата
    /// десинка по адресу не вывести.
    /// </remarks>
    public static string? FakeRange(string configPath)
    {
        try
        {
            var servers = JsonNode.Parse(File.ReadAllText(configPath))?["dns"]?["servers"] as JsonArray;

            return (servers ?? [])
                .OfType<JsonObject>()
                .Where(s => (string?)s["type"] == "fakeip")
                .Select(s => (string?)s["inet4_range"])
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Куда подключается туннель: <c>server</c> у выходов и адреса пиров WireGuard.
    /// </summary>
    internal static IReadOnlyList<string> Hosts(JsonNode? config, string? first = null)
    {
        var hosts = new List<string>();

        foreach (var outbound in (config?["outbounds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)outbound["server"] is not { Length: > 0 } server)
                continue;

            if (first is not null && (string?)outbound["tag"] == first)
                hosts.Insert(0, server);
            else
                hosts.Add(server);
        }

        foreach (var endpoint in (config?["endpoints"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var peer in (endpoint["peers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)peer["address"] is { Length: > 0 } address)
                    hosts.Add(address);
            }
        }

        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
