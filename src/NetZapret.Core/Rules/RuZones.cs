namespace NetZapret.Core.Rules;

/// <summary>
/// Российские доменные зоны — напрямую, если ни одно правило не решило иначе.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 15.10: «.ru, .su, .рф изначально идут напрямую даже в режиме
/// туннель, если нет никаких доп правил». Российские сервисы через
/// зарубежный выход требуют подтверждений или отказывают, а список
/// российских сетей (ipset-ru, 658 сетей) ловит не всех: в «Наблюдении»
/// владельца 15.10 из 27 имён в этих зонах 9 стояли на адресах вне него —
/// среди них серверы проверки государственных сертификатов
/// (nuc-cdp.digital.gov.ru, nuc-cdp.voskhod.ru), и в «Туннеле» они шли
/// через VPN.
/// </para>
/// <para>
/// Нижний слой (<see cref="RuleSource.Fallback"/>): ниже всех правил всех
/// классов, и своих по адресам тоже, — так и выходит «если нет других
/// правил». Заблокированные российские сайты решает маршрут «Реестр
/// блокировок РФ» выше (37 тысяч имён в этих зонах): в «Гибриде» они остаются
/// под десинком дырами в щите (HostsFile.CollectShieldHoles), в «Туннеле»
/// идут через VPN. «Без исключений» отменяет и это правило, как все «напрямую».
/// </para>
/// <para>
/// .рф в именах — уже как xn--p1ai: так его видят DNS, TLS и движки.
/// </para>
/// </remarks>
public static class RuZones
{
    public static IReadOnlyList<string> Zones { get; } = ["ru", "su", "xn--p1ai"];

    /// <summary>Лежит ли имя в российской зоне.</summary>
    public static bool Covers(string name)
    {
        var host = name.TrimStart('*', '.').TrimEnd('.');

        foreach (var zone in Zones)
        {
            if (host.Equals(zone, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Правила для движка: каждая зона с поддоменами — напрямую, нижним слоем.</summary>
    public static IReadOnlyList<RoutingRule> Rules() =>
        Zones.Select(zone => new RoutingRule
        {
            Match = MatchKind.Domain,
            Value = "*." + zone,
            Mode = RoutingMode.Direct,
            Source = RuleSource.Fallback,
        }).ToList();
}
