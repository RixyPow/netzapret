using NetZapret.Core;

namespace NetZapret.Zapret;

/// <summary>
/// Ширина перехвата поверх пресета (<see cref="CaptureWidth"/>).
/// </summary>
/// <remarks>
/// <para>
/// Перехват задают ключи <c>--wf-tcp-out</c> и <c>--wf-udp-out</c>: что
/// под них попало, проходит через winws2 пакет за пакетом, даже если ни одна
/// секция его не тронет. Замер 03.10 — в <see cref="CaptureWidth"/>.
/// </para>
/// <para>
/// Части фильтра пресета по содержимому (<c>--wf-raw-part</c>: STUN,
/// обнаружение адреса Discord, WireGuard) не трогаются ни на каком уровне —
/// они ловят свои пакеты на любом порту, и звонки от уровня не зависят.
/// Пресет с полным фильтром (<c>--wf-raw</c>) уровню не поддаётся вовсе:
/// ключи портов winws2 при нём не читает.
/// </para>
/// </remarks>
public static class PresetCapture
{
    private const string TcpKey = "--wf-tcp-out=";
    private const string UdpKey = "--wf-udp-out=";
    private const string RawKey = "--wf-raw=";

    /// <summary>Уровень словами — для окна и журнала.</summary>
    public static string Word(CaptureWidth width) => width switch
    {
        CaptureWidth.Sites => "только сайты",
        CaptureWidth.All => "всё",
        _ => "как в пресете",
    };

    /// <summary>Порты уровня; <c>null</c> — как в пресете.</summary>
    public static (string Tcp, string Udp)? PortsOf(CaptureWidth width) => width switch
    {
        CaptureWidth.Sites => ("80,443", "443"),
        CaptureWidth.All => ("80,443-65535", "443-65535"),
        _ => null,
    };

    /// <summary>Поддаётся ли строка уровню — нет ли в ней полного фильтра.</summary>
    public static bool Applies(IReadOnlyList<string> arguments) =>
        !arguments.Any(a => a.StartsWith(RawKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ставит порты уровня в строку запуска; «как в пресете» — не трогает ничего.</summary>
    /// <remarks>
    /// Ключ на месте — меняется значение, места он не меняет; ключа нет —
    /// заводится в начале: глобальные ключи от места не зависят
    /// (<see cref="WinwsCommandLine"/>, SplitGlobals).
    /// </remarks>
    public static void Apply(List<string> arguments, CaptureWidth width)
    {
        if (PortsOf(width) is not { } ports || !Applies(arguments))
            return;

        Set(arguments, TcpKey, ports.Tcp);
        Set(arguments, UdpKey, ports.Udp);
    }

    private static void Set(List<string> arguments, string key, string value)
    {
        int index = arguments.FindIndex(a => a.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            arguments.Insert(0, key + value);
        else
            arguments[index] = key + value;
    }

    /// <summary>Порты перехвата при этом уровне: TCP и UDP; <c>null</c> — полный фильтр, портов нет.</summary>
    public static (string Tcp, string Udp)? Effective(ZapretPreset preset, CaptureWidth width)
    {
        if (!Applies(preset.GlobalArguments))
            return null;

        if (PortsOf(width) is { } ports)
            return ports;

        return (Value(preset.GlobalArguments, TcpKey) ?? string.Empty, Value(preset.GlobalArguments, UdpKey) ?? string.Empty);
    }

    /// <summary>
    /// Секции, часть объявленных портов которых при этом уровне вне перехвата.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Строкой «секция — что теряет», для окна. Перехват уже, чем объявила
    /// секция, — и трафик на остальных портах до неё не доходит вовсе, а снаружи
    /// это неотличимо от неработающего рецепта.
    /// </para>
    /// <para>
    /// По TCP называются только отдельные порты и короткие диапазоны (до 64).
    /// Широкое «443-65535» у секции по именам значит «TLS на любом порту», и
    /// потеря его хвоста почти ничего не стоит, а список из двадцати секций
    /// «теряет 444–65535» заслонил бы то, что важно, — запасные порты Discord,
    /// 5222 у Telegram. По UDP — всё: UDP-секции пишутся под конкретный трафик,
    /// голос или игры, и потеря любого куска — потеря именно его.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Unreached(ZapretPreset preset, CaptureWidth width)
    {
        if (Effective(preset, width) is not { } ports)
            return [];

        var tcpCaptured = Ranges(ports.Tcp);
        var udpCaptured = Ranges(ports.Udp);
        var found = new List<string>();

        foreach (var section in preset.Sections)
        {
            var lost = new List<string>();

            var tcpLost = Outside(Ranges(Filter(section, "--filter-tcp=")), tcpCaptured)
                .Where(r => r.Last - r.First < 64)
                .ToList();

            var udpLost = Outside(Ranges(Filter(section, "--filter-udp=")), udpCaptured).ToList();

            if (tcpLost.Count > 0)
                lost.Add("TCP " + Text(tcpLost));

            if (udpLost.Count > 0)
                lost.Add("UDP " + Text(udpLost));

            if (lost.Count > 0)
                found.Add($"{(string.IsNullOrWhiteSpace(section.Name) ? "без имени" : section.Name)} — {string.Join(", ", lost)}");
        }

        return found;
    }

    private static string? Value(IReadOnlyList<string> arguments, string key) =>
        arguments.LastOrDefault(a => a.StartsWith(key, StringComparison.OrdinalIgnoreCase))?[key.Length..];

    private static string Filter(ZapretSection section, string key) =>
        string.Join(',', section.RawArguments
            .Where(a => a.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .Select(a => a[key.Length..]));

    /// <summary>Порты строкой winws2 — «80,443-65535», «*», «~22» — диапазонами.</summary>
    /// <remarks>Отрицание (<c>~</c>) пропускается: оно сужает, а не объявляет.</remarks>
    internal static List<(int First, int Last)> Ranges(string ports)
    {
        var result = new List<(int, int)>();

        foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "*")
            {
                result.Add((1, 65535));
                continue;
            }

            if (part.StartsWith('~'))
                continue;

            var bounds = part.Split('-');

            if (bounds.Length == 1 && int.TryParse(bounds[0], out int one))
                result.Add((one, one));
            else if (bounds.Length == 2 && int.TryParse(bounds[0], out int low) && int.TryParse(bounds[1], out int high) && low <= high)
                result.Add((low, high));
        }

        return result;
    }

    /// <summary>Что из объявленного не покрыто перехватом.</summary>
    private static IEnumerable<(int First, int Last)> Outside(
        List<(int First, int Last)> declared, List<(int First, int Last)> captured)
    {
        foreach (var range in declared)
        {
            int from = range.First;

            foreach (var taken in captured.Where(c => c.Last >= range.First && c.First <= range.Last).OrderBy(c => c.First))
            {
                if (taken.First > from)
                    yield return (from, Math.Min(taken.First - 1, range.Last));

                from = Math.Max(from, taken.Last + 1);

                if (from > range.Last)
                    break;
            }

            if (from <= range.Last)
                yield return (from, range.Last);
        }
    }

    private static string Text(IEnumerable<(int First, int Last)> ranges) =>
        string.Join(", ", ranges.Select(r => r.First == r.Last ? $"{r.First}" : $"{r.First}–{r.Last}"));
}
