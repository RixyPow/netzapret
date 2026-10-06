using System.Net;
using System.Net.Sockets;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetZapret.Proxy;

/// <summary>Имена, прибиваемые к одному набору адресов.</summary>
public sealed record PinSolutionGroup(IReadOnlyList<string> Addresses, IReadOnlyList<string> Names);

/// <summary>Готовое решение с пином: сервис, его имена и адреса.</summary>
public sealed record PinSolution
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Пояснение для человека: чьи адреса и чем грозит.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Адреса принадлежат самому сервису: пину нужен десинк (<see cref="HostsEditor.DesyncMark"/>).
    /// </summary>
    /// <remarks><c>false</c> — адреса посредника, и щит держит такой пин, как любой другой.</remarks>
    public bool Desync { get; init; } = true;

    /// <summary>Без IPv6 в сети решение не работает (у Zapret GUI — «работает, если есть IPv6»).</summary>
    public bool NeedsIpV6 { get; init; }

    public required IReadOnlyList<PinSolutionGroup> Pins { get; init; }

    /// <summary>Все имена решения, без повторов.</summary>
    public IReadOnlyList<string> Names =>
        Pins.SelectMany(p => p.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Насколько решение стоит в нашем блоке hosts.</summary>
/// <param name="Pinned">Сколько его имён прибито к его же адресам.</param>
/// <param name="Total">Сколько имён в решении.</param>
/// <param name="Elsewhere">Сколько его имён прибито нами к чужим адресам — их снятие не тронет.</param>
public sealed record PinSolutionState(int Pinned, int Total, int Elsewhere)
{
    public bool On => Pinned > 0;

    public bool Partly => Pinned > 0 && Pinned < Total;
}

/// <summary>
/// Готовые решения с пином — раздел «Напрямую» каталога hosts Zapret GUI.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 06.10: «создадим раздел в настройках маршрутов с этими
/// решениями». Это пины, где адрес прописывается как есть: Instagram
/// на адреса Meta, X на адреса Twitter, Discord и его голос на узлы
/// Cloudflare. Почти все — адреса самого сервиса, и им нужен десинк
/// поверх пина; до 06.10 щит брал любой пин, и такое решение у нас
/// не работало бы вовсе (№18, JohnnyTargo). См. <see cref="HostsEditor.DesyncMark"/>.
/// </para>
/// <para>
/// Свой файл, а не каталог Zapret GUI на лету: программа обязана работать
/// без него, а списки у нас только свои. Снят 06.10 с каталога 2026.10.01.1
/// и заменяется при обновлении программы (<c>UpdateInstaller</c> сохраняет
/// только catalog.yaml и addresses.yaml).
/// </para>
/// </remarks>
public static class PinSolutions
{
    public static string DefaultPath => Path.Combine("config", "pin-solutions.yaml");

    /// <summary>Читает решения; пусто, если файла нет или он не разобрался.</summary>
    public static IReadOnlyList<PinSolution> Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        if (!File.Exists(target))
            return [];

        try
        {
            var dto = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<FileDto>(File.ReadAllText(target));

            return (dto?.Solutions ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new PinSolution
                {
                    Id = s.Id!.Trim(),
                    Name = s.Name!.Trim(),
                    Note = string.IsNullOrWhiteSpace(s.Note) ? null : s.Note.Trim(),
                    Desync = s.Desync ?? true,
                    NeedsIpV6 = string.Equals(s.Needs?.Trim(), "ipv6", StringComparison.OrdinalIgnoreCase),
                    Pins = (s.Pins ?? [])
                        .Select(p => new PinSolutionGroup(
                            (p.Addresses ?? []).Select(a => a.Trim()).Where(a => IPAddress.TryParse(a, out _)).ToList(),
                            (p.Names ?? []).Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).ToList()))
                        .Where(g => g.Addresses.Count > 0 && g.Names.Count > 0)
                        .ToList(),
                })
                .Where(s => s.Pins.Count > 0)
                .ToList();
        }
        catch (Exception)
        {
            // Испорченный файл — не повод ронять окно: раздел просто пуст.
            return [];
        }
    }

    /// <summary>
    /// Что писать в hosts: имя — его адреса, без IPv6, если его нет в сети.
    /// </summary>
    /// <remarks>
    /// Адрес IPv6 без IPv6 в сети не помогает, а мешает: Windows пробует его
    /// первым, ждёт и только потом берёт IPv4. Имя, у которого остались одни
    /// адреса IPv6, не пишется вовсе.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Entries(PinSolution solution, bool haveIpV6)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in solution.Pins)
        {
            foreach (var name in group.Names)
            {
                foreach (var address in group.Addresses)
                {
                    if (!haveIpV6 && IsV6(address))
                        continue;

                    if (!result.TryGetValue(name, out var list))
                        result[name] = list = [];

                    if (!list.Contains(address, StringComparer.OrdinalIgnoreCase))
                        list.Add(address);
                }
            }
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Можно ли включить решение в этой сети; <c>null</c> — можно, иначе почему нет.</summary>
    public static string? Unavailable(PinSolution solution, bool haveIpV6) =>
        solution.NeedsIpV6 && !haveIpV6 ? "Нужен IPv6 — сейчас его нет"
        : Entries(solution, haveIpV6).Count == 0 ? "Без IPv6 в нём не остаётся ни одного адреса"
        : null;

    /// <summary>Сколько имён решения стоит в нашем блоке, и к каким адресам.</summary>
    public static PinSolutionState StateOf(PinSolution solution, string? hostsPath = null)
    {
        var ours = HostsEditor.PinsAll(hostsPath);
        var all = Entries(solution, haveIpV6: true);

        int pinned = 0, elsewhere = 0;

        foreach (var (name, addresses) in all)
        {
            if (!ours.TryGetValue(name, out var now) || now.Count == 0)
                continue;

            if (now.All(a => addresses.Contains(a, StringComparer.OrdinalIgnoreCase)))
                pinned++;
            else
                elsewhere++;
        }

        return new PinSolutionState(pinned, all.Count, elsewhere);
    }

    /// <summary>Прибивает имена решения в нашем блоке — с пометкой десинка, если он нужен.</summary>
    public static PinResult Enable(PinSolution solution, bool haveIpV6, string? hostsPath = null)
    {
        if (Unavailable(solution, haveIpV6) is { } why)
            throw new InvalidOperationException(why);

        var entries = Entries(solution, haveIpV6);

        return solution.Desync
            ? HostsEditor.PinWithDesync(entries, hostsPath)
            : HostsEditor.Repin(entries, hostsPath);
    }

    /// <summary>
    /// Снимает имена решения — только те, что прибиты к его же адресам.
    /// </summary>
    /// <remarks>
    /// Имя, которое человек после включения перебил своим пином (окно пина,
    /// посредник), — уже его выбор, и снимать его вместе с решением нельзя.
    /// </remarks>
    public static PinResult Disable(PinSolution solution, string? hostsPath = null)
    {
        var ours = HostsEditor.PinsAll(hostsPath);
        var all = Entries(solution, haveIpV6: true);

        var drop = all
            .Where(p => ours.TryGetValue(p.Key, out var now) && now.Count > 0
                && now.All(a => p.Value.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .Select(p => p.Key)
            .ToList();

        return drop.Count == 0
            ? new PinResult { Pinned = ours.Count, Shadowed = [] }
            : HostsEditor.Unpin(drop, hostsPath);
    }

    /// <summary>
    /// Имена решения, которым маршрут не даст десинка, — с причиной.
    /// </summary>
    /// <remarks>
    /// Пин с пометкой щит не берёт, но правило «напрямую» и «через VPN» при
    /// выключенном туннеле выводят имя из-под десинка и так — и решение
    /// молча не заработает. «Через VPN» при поднятом туннеле — не помеха:
    /// прибитый адрес уходит в туннель (HostsFile.CollectPinnedProxy).
    /// </remarks>
    public static IReadOnlyList<(string Name, DesyncBypass Why)> RouteBlockers(
        PinSolution solution,
        Core.Rules.RuleSet ruleSet,
        bool tunnelUp,
        string? hostsPath = null)
    {
        if (!solution.Desync)
            return [];

        var exclusions = HostsFile.DescribeDesyncExclusions(ruleSet, hostsPath, tunnelUp);

        return solution.Names
            .Select(name => (Name: name, Why: HostsFile.BypassFor(exclusions, name)))
            .Where(p => p.Why is DesyncBypass.Direct or DesyncBypass.VpnWithoutTunnel)
            .ToList();
    }

    private static bool IsV6(string address) =>
        IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;

    private sealed class FileDto
    {
        public List<SolutionDto>? Solutions { get; set; }
    }

    private sealed class SolutionDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Note { get; set; }
        public bool? Desync { get; set; }
        public string? Needs { get; set; }
        public List<GroupDto>? Pins { get; set; }
    }

    private sealed class GroupDto
    {
        public List<string>? Addresses { get; set; }
        public List<string>? Names { get; set; }
    }
}
