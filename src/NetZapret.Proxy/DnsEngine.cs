using NetZapret.Core;
using NetZapret.Core.Rules;

namespace NetZapret.Proxy;

/// <summary>
/// Движок туннеля без выхода — только чтобы отвечать на DNS Windows при одном десинке.
/// </summary>
/// <remarks>
/// <para>
/// Когда и зачем — <see cref="AppSettings.NeedsDnsEngine"/>: открытый DNS
/// у части операторов подменяется (замер 03.10), а без туннеля имена разрешает
/// сама Windows.
/// </para>
/// <para>
/// Устроен как выборочный туннель без единого сервера. В TUN заводятся только
/// адреса резолверов Windows, их запросы забирает <c>hijack-dns</c>, и движок
/// спрашивает выбранный резолвер по DoH напрямую. DoH самой Windows к тем же
/// адресам отклоняется — она откатывается на UDP, который мы видим. Подменных
/// адресов никто не получает: правил «через VPN» здесь нет, весь прочий трафик
/// туннеля не касается и идёт к десинку, как без движка.
/// </para>
/// <para>
/// Правила человека сюда не берутся вовсе. Маршрутов у такого движка нет —
/// ему некуда вести, — и чем меньше он знает, тем меньше в нём может разойтись
/// с тем, что делает десинк.
/// </para>
/// </remarks>
public static class DnsEngine
{
    /// <summary>
    /// Имя службы у надзора.
    /// </summary>
    /// <remarks>
    /// Не «sing-box»: окно, вкладка VPN, замер скорости и трей узнают туннель
    /// по этому имени, и движок ради DNS показался бы им работающим туннелем.
    /// </remarks>
    public const string ServiceName = "DNS";

    private static readonly RuleSet NoRoutes = new()
    {
        Rules = [],
        DefaultMode = RoutingMode.Desync,
        Operating = OperatingMode.DesyncOnly,
    };

    /// <summary>Конфиг движка, который только отвечает на DNS.</summary>
    /// <param name="systemResolvers">Резолверы Windows префиксами (<see cref="SystemResolvers.Discover"/>).</param>
    /// <param name="addressOverrides">Подставленные адреса — как у туннеля, нашим резолвером.</param>
    public static CompilationResult Compile(
        AppSettings settings,
        IReadOnlyList<string> systemResolvers,
        IReadOnlyDictionary<string, string> addressOverrides,
        EngineKeys keys)
    {
        var provider = DnsSurvey.ByAddress(settings.DnsServer);

        return new SingBoxConfigCompiler().Compile(NoRoutes, [], new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = systemResolvers,
            DnsServer = settings.DnsServer,
            DnsServerName = provider?.TlsName,
            DnsServerPath = provider?.DohPath,

            // Туннеля нет: «через туннель» в настройках здесь означает
            // «через движок», а сам запрос к резолверу идёт напрямую.
            DnsThroughTunnel = false,

            // Вход проверки вёл бы в никуда: выхода нет, final — direct,
            // и замер «через туннель» мерил бы домашнюю сеть.
            HealthInbound = false,
            Keys = keys,
            AddressOverrides = addressOverrides,
        });
    }
}
