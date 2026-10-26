using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetZapret.Zapret;

/// <summary>
/// Списки имён и адресов в том виде, в каком их читает ByeDPI (<c>-H</c>, <c>-j</c>).
/// </summary>
/// <remarks>
/// <para>
/// Наши списки ByeDPI как есть не годятся (разбор <c>parse_hosts</c>
/// и <c>parse_ipset</c> в <c>main.c</c> hufrea/byedpi, main@ba532298).
/// Комментариев у него нет: строка <c># см. github.com</c> даёт группе домен
/// <c>github.com</c>, а слово <c>#</c> он молча выбросит. Разделители — только
/// пробел и перевод строки. Слово с любым знаком вне <c>[A-Za-z0-9.-/]</c>
/// выбрасывается целиком: <c>_</c>, <c>*</c>, кириллица. Подсчёт 27.10:
/// комментарии есть в 116 из 117 своих списков и в 30 из 133 списков Zapret GUI.
/// </para>
/// <para>
/// Поэтому список чистится здесь, а отброшенное называется: ByeDPI выбросил бы
/// его сам, но строкой в stderr, которую никто не читает.
/// </para>
/// </remarks>
internal static class ByeDpiLists
{
    /// <summary>
    /// Имена списка: без комментариев, без <c>*.</c> и ведущей точки, строчными,
    /// IDN — в punycode; негодные — в <paramref name="dropped"/>.
    /// </summary>
    public static List<string> Names(IEnumerable<string> lines, ICollection<string>? dropped)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in Words(lines))
        {
            var name = Name(word);

            if (name is null)
            {
                dropped?.Add(word);
                continue;
            }

            if (seen.Add(name))
                result.Add(name);
        }

        return result;
    }

    /// <summary>Адреса и подсети списка в виде «сеть/префикс»; негодные — в <paramref name="dropped"/>.</summary>
    public static List<string> Addresses(IEnumerable<string> lines, ICollection<string>? dropped)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in Words(lines))
        {
            var cidr = Cidr.Parse(word);

            if (cidr is null)
            {
                dropped?.Add(word);
                continue;
            }

            var text = cidr.Value.ToString();

            if (seen.Add(text))
                result.Add(text);
        }

        return result;
    }

    /// <summary>По записи на строку, <c>\n</c>, с переводом строки в конце; пусто — пустая строка.</summary>
    public static string Join(IReadOnlyCollection<string> entries) =>
        entries.Count == 0 ? string.Empty : string.Join('\n', entries) + "\n";

    /// <summary>
    /// Слова списка: всё после <c>#</c> — комментарий, как у
    /// <c>PresetReader.ReadListFile</c>; слова делятся любым пробельным знаком.
    /// </summary>
    /// <remarks>
    /// Делить строку на слова, а не брать целиком, — затем, что так её прочтёт
    /// и ByeDPI: «a.com b.com» у него два имени. Табуляцию он разделителем
    /// не считает и выбросил бы слово с ней — здесь она делит, как пробел.
    /// </remarks>
    private static IEnumerable<string> Words(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw;
            int hash = line.IndexOf('#');

            if (hash >= 0)
                line = line[..hash];

            foreach (var word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = word.Trim('﻿');

                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }
    }

    private static string? Name(string word)
    {
        var name = word.Trim().TrimEnd('.');

        if (name.StartsWith("*.", StringComparison.Ordinal))
            name = name[2..];

        name = name.TrimStart('.');
        name = name.ToLowerInvariant();

        if (name.Length == 0)
            return null;

        if (!name.All(char.IsAscii))
            name = Ascii(name) ?? string.Empty;

        if (name.Length == 0 || name.Length > 253)
            return null;

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
                return null;
        }

        // «a..b», «-a.com» и чистые точки ByeDPI проглотил бы, но совпасть
        // им не с чем — это опечатки списка, а не имена.
        if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('-') || name.EndsWith('-'))
            return null;

        return name;
    }

    /// <summary>
    /// Имя с не-ASCII знаками — в punycode.
    /// </summary>
    /// <remarks>
    /// Сперва <see cref="IdnMapping"/>: на Windows он работает и при
    /// <c>InvariantGlobalization</c> (проверено 27.10: «пример.рф» →
    /// xn--e1afmkfd.xn--p1ai), но регистр сам не сводит — «ПРИМЕР.РФ» дал
    /// бы другое имя, поэтому строчными имя делается раньше. Как он ведёт себя
    /// на Android без ICU, не проверено, — отсюда свой кодировщик RFC 3492
    /// запасным путём: метки имени кодируются по одной.
    /// </remarks>
    internal static string? Ascii(string name)
    {
        try
        {
            return new IdnMapping().GetAscii(name).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
        }

        var labels = name.Split('.');
        var result = new StringBuilder();

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0)
                result.Append('.');

            var label = labels[i];

            if (label.All(char.IsAscii))
            {
                result.Append(label);
                continue;
            }

            var encoded = Punycode(label);

            if (encoded is null)
                return null;

            result.Append("xn--").Append(encoded);
        }

        return result.ToString();
    }

    /// <summary>Кодирование метки по RFC 3492 (раздел 6.3); <c>null</c> — переполнение.</summary>
    internal static string? Punycode(string label)
    {
        const int Base = 36, TMin = 1, TMax = 26, Skew = 38, Damp = 700, InitialBias = 72, InitialN = 128;

        var points = new List<int>();

        for (int i = 0; i < label.Length; i++)
        {
            int cp = char.ConvertToUtf32(label, i);
            points.Add(cp);

            if (char.IsHighSurrogate(label[i]))
                i++;
        }

        var output = new StringBuilder();

        foreach (var cp in points.Where(p => p < 0x80))
            output.Append((char)cp);

        int basic = output.Length;
        int handled = basic;

        if (basic > 0)
            output.Append('-');

        int n = InitialN, delta = 0, bias = InitialBias;

        while (handled < points.Count)
        {
            int m = points.Where(p => p >= n).Min();

            if (m - n > (int.MaxValue - delta) / (handled + 1))
                return null;

            delta += (m - n) * (handled + 1);
            n = m;

            foreach (var cp in points)
            {
                if (cp < n && ++delta == 0)
                    return null;

                if (cp != n)
                    continue;

                int q = delta;

                for (int k = Base; ; k += Base)
                {
                    int t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;

                    if (q < t)
                        break;

                    output.Append(Digit(t + (q - t) % (Base - t)));
                    q = (q - t) / (Base - t);
                }

                output.Append(Digit(q));
                bias = Adapt(delta, handled + 1, handled == basic);
                delta = 0;
                handled++;
            }

            delta++;
            n++;
        }

        return output.ToString();

        static char Digit(int d) => (char)(d < 26 ? 'a' + d : '0' + d - 26);

        static int Adapt(int delta, int points, bool first)
        {
            delta = first ? delta / Damp : delta / 2;
            delta += delta / points;
            int k = 0;

            while (delta > ((Base - TMin) * TMax) / 2)
            {
                delta /= Base - TMin;
                k += Base;
            }

            return k + (Base - TMin + 1) * delta / (delta + Skew);
        }
    }
}

/// <summary>
/// Множество имён для вопроса «пересекаются ли два списка».
/// </summary>
/// <remarks>
/// <para>
/// Пересекаются — значит, есть имя, которое заберут оба: одна запись равна
/// другой или лежит под ней. Запись покрывает свою зону по границе метки,
/// как у ByeDPI (<c>check_host</c>) и у файлового <c>--hostlist</c> winws2
/// (<see cref="PresetZones"/>).
/// </para>
/// <para>
/// Своя цепочка зон, а не <see cref="HostNames.ZoneChain"/>: та
/// останавливается на двух метках, а ByeDPI сравнивает и с однометочной
/// записью — «ru» в списке забрал бы всю зону. Для вопроса «заберут ли оба»
/// важна именно его семантика.
/// </para>
/// </remarks>
internal sealed class NameZones
{
    private readonly HashSet<string> _names;
    private HashSet<string>? _ancestors;

    public NameZones(IEnumerable<string> names) =>
        _names = new HashSet<string>(names, StringComparer.Ordinal);

    public int Count => _names.Count;

    public IEnumerable<string> Names => _names;

    /// <summary>Само имя и все его зоны, до однометочной включительно.</summary>
    public static IEnumerable<string> Suffixes(string name)
    {
        yield return name;

        for (int dot = name.IndexOf('.'); dot >= 0 && dot < name.Length - 1; dot = name.IndexOf('.', dot + 1))
            yield return name[(dot + 1)..];
    }

    /// <summary>Есть ли в множестве запись, покрывающая это имя.</summary>
    public bool Covers(string name) => Suffixes(name).Any(_names.Contains);

    /// <summary>Есть ли в множестве запись, лежащая под этим именем (или равная ему).</summary>
    public bool HasUnder(string name) => Ancestors().Contains(name);

    /// <summary>Заберут ли оба множества хоть одно общее имя.</summary>
    /// <remarks>
    /// Обходится меньшее множество: каждое его имя проверяется в обе стороны —
    /// покрыто ли записью большего и нет ли записи большего под ним. Второе
    /// держится на множестве всех зон большего, собранном один раз: реестр
    /// блокировок (117 тысяч имён) иначе обходился бы на каждую проверку.
    /// </remarks>
    public bool Intersects(NameZones other)
    {
        var (small, large) = Count <= other.Count ? (this, other) : (other, this);

        foreach (var name in small._names)
        {
            if (large.Covers(name) || large.HasUnder(name))
                return true;
        }

        return false;
    }

    private HashSet<string> Ancestors()
    {
        if (_ancestors is not null)
            return _ancestors;

        var all = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in _names)
        {
            foreach (var suffix in Suffixes(name))
            {
                if (!all.Add(suffix))
                    break;
            }
        }

        return _ancestors = all;
    }
}

/// <summary>Подсеть IPv4 или IPv6: начало и конец диапазона.</summary>
internal readonly record struct Cidr(bool V6, UInt128 First, UInt128 Last, IPAddress Network, int Prefix)
{
    public override string ToString() => $"{Network}/{Prefix}";

    /// <summary>
    /// Адрес или подсеть; <c>null</c> — не адрес или <c>/0</c>.
    /// </summary>
    /// <remarks>
    /// <c>/0</c> отбрасывается, как и у ByeDPI (<c>parse_ip</c>: «bits &lt;= 0» —
    /// запись негодна): весь интернет в списке адресов — всегда ошибка списка.
    /// Хвост адреса за маской обнуляется — у сети одно написание, и две записи
    /// одной сети не расходятся.
    /// </remarks>
    public static Cidr? Parse(string text)
    {
        var value = text.Trim();
        int slash = value.IndexOf('/');
        var head = slash >= 0 ? value[..slash] : value;

        if (!IPAddress.TryParse(head, out var address) || head.Contains('%'))
            return null;

        bool v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        int bits = v6 ? 128 : 32;
        int prefix = bits;

        if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                || prefix <= 0 || prefix > bits))
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        UInt128 number = 0;

        foreach (var b in bytes)
            number = (number << 8) | b;

        UInt128 hostMask = prefix == bits ? 0 : (UInt128.One << (bits - prefix)) - 1;
        var first = number & ~hostMask;

        if (!v6)
            first &= uint.MaxValue;

        var last = first | hostMask;
        var network = new byte[bytes.Length];
        var rest = first;

        for (int i = network.Length - 1; i >= 0; i--)
        {
            network[i] = (byte)(rest & 0xFF);
            rest >>= 8;
        }

        return new Cidr(v6, first, last, new IPAddress(network), prefix);
    }
}

/// <summary>Множество подсетей для вопроса «пересекаются ли два списка адресов».</summary>
internal sealed class CidrSet
{
    private readonly Cidr[] _ranges;

    public CidrSet(IEnumerable<Cidr> ranges) =>
        _ranges = ranges.OrderBy(r => r.V6).ThenBy(r => r.First).ToArray();

    public static CidrSet From(IEnumerable<string> entries) =>
        new(entries.Select(Cidr.Parse).Where(c => c is not null).Select(c => c!.Value));

    /// <summary>Есть ли адрес, входящий в оба множества.</summary>
    public bool Intersects(CidrSet other)
    {
        int i = 0, j = 0;
        var a = _ranges;
        var b = other._ranges;

        while (i < a.Length && j < b.Length)
        {
            var x = a[i];
            var y = b[j];

            if (x.V6 != y.V6)
            {
                if (!x.V6)
                    i++;
                else
                    j++;

                continue;
            }

            if (x.Last < y.First)
            {
                i++;
                continue;
            }

            if (y.Last < x.First)
            {
                j++;
                continue;
            }

            return true;
        }

        return false;
    }
}
