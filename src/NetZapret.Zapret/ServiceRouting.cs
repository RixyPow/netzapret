using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Zapret;

/// <summary>
/// Куда сейчас идёт часть сервиса и как это изменить.
/// </summary>
/// <remarks>
/// <para>
/// Сервис — способ показа и редактирования, а не новая сущность. Выбор
/// «голос Discord через VPN» разворачивается в обычное правило
/// <c>match: hostlist, value: lists/discord-media.txt</c> в пользовательском
/// слое. Второго формата, который пришлось бы согласовывать с первым,
/// не появляется.
/// </para>
/// <para>
/// Нынешний режим не хранится, а вычисляется: берётся домен из списка
/// и прогоняется через тот же движок правил, что решает судьбу настоящих
/// соединений. Поэтому список не врёт даже тогда, когда правило написано
/// руками или пришло из базового набора.
/// </para>
/// </remarks>
public static class ServiceRouting
{
    /// <summary>Положение одной части сервиса.</summary>
    public sealed record PartStatus
    {
        public required ServicePart Part { get; init; }

        /// <summary>Куда пойдёт трафик этой части.</summary>
        public required RoutingMode Mode { get; init; }

        /// <summary>Доменов в списке; ноль означает, что список не нашёлся.</summary>
        public required int DomainCount { get; init; }

        /// <summary>Правило задано человеком, а не унаследовано.</summary>
        public required bool Explicit { get; init; }

        /// <summary>Первый домен списка — чтобы было видно, о чём речь.</summary>
        public string? Example { get; init; }

        public string DescribeMode() => Mode switch
        {
            RoutingMode.Proxy => "VPN",
            RoutingMode.Desync => "десинк",
            _ => "напрямую",
        };
    }

    /// <summary>
    /// Определяет, куда сейчас идёт каждая часть сервиса.
    /// </summary>
    public static IReadOnlyList<PartStatus> Describe(
        ServiceDefinition service,
        RuleEngine engine,
        string? zapretRoot,
        UserRulesFile userRules)
    {
        var result = new List<PartStatus>();

        foreach (var part in service.Parts)
        {
            var domains = HostListReader.Read(part.List, zapretRoot, out _);

            if (domains.Count == 0)
            {
                result.Add(new PartStatus
                {
                    Part = part,
                    Mode = RoutingMode.Direct,
                    DomainCount = 0,
                    Explicit = false,
                });

                continue;
            }

            bool set = userRules.Entries.Any(e =>
                e.Match == MatchKind.HostList && e.Enabled && e.Matches(part.List));

            result.Add(new PartStatus
            {
                Part = part,
                Mode = ModeFor(domains[0], engine),
                DomainCount = domains.Count,
                Explicit = set,
                Example = domains[0],
            });
        }

        return result;
    }

    /// <summary>
    /// Куда движок отправит соединение к этому домену.
    /// </summary>
    /// <remarks>
    /// Через настоящий движок, а не поиском правила по совпадению строк:
    /// иначе показанное разошлось бы с действительным ровно в тех случаях,
    /// ради которых список и смотрят — при перекрытии правил.
    /// </remarks>
    private static RoutingMode ModeFor(string domain, RuleEngine engine)
    {
        var host = domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;

        return engine.Evaluate(new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,

            // Адрес не подставляется намеренно. Мы спрашиваем про домен,
            // адреса у него сейчас нет, и выдуманный не остаётся без
            // последствий: IPAddress.None — это 255.255.255.255, и он попадал
            // в список российских подсетей, отчего все сервисы показывались
            // как идущие напрямую. Верным оставался только RuTracker, и лишь
            // потому, что доменные правила проверяются раньше адресных.
            RemoteAddress = null,
            RemotePort = 443,
            Hostname = host,
        }).Mode;
    }

    /// <summary>
    /// Общее описание сервиса: одинаково ли идут его части.
    /// </summary>
    public static string Summarize(IReadOnlyList<PartStatus> parts)
    {
        var known = parts.Where(p => p.DomainCount > 0).ToList();

        if (known.Count == 0)
            return "списки не найдены";

        var modes = known.Select(p => p.Mode).Distinct().ToList();

        if (modes.Count == 1)
            return $"всё {known[0].DescribeMode()}";

        // Разные части идут по-разному — ровно тот случай, ради которого
        // разделение и заводилось. Перечисляем, а не прячем за «смешанно».
        return string.Join(", ", known.Select(p => $"{p.Part.Name.ToLowerInvariant()} {p.DescribeMode()}"));
    }
}
