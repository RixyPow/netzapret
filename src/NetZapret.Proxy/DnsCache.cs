using System.Runtime.InteropServices;

namespace NetZapret.Proxy;

/// <summary>
/// Имена из кэша DNS Windows — то, что система недавно спрашивала.
/// </summary>
/// <remarks>
/// <para>
/// Нужен пину. Файл hosts зон не знает: каждое имя прибивается отдельно,
/// а какие имена нужны сайту, из списка сервиса не видно. У crunchyroll
/// в списке одна зона, а страница живёт ещё на sso, beta-api и static —
/// замер 23.09: прибиты были только голое имя и www, и страница вставала
/// пустой, хотя через посредника и sso (200), и beta-api (401 «invalid
/// token», то есть ответил сам API) работают.
/// </para>
/// <para>
/// Открытый и не загрузившийся сайт оставляет в кэше всё, что спрашивал, —
/// оттуда окно пина и берёт имена под зонами сервиса. Прав администратора
/// чтение не требует.
/// </para>
/// </remarks>
public static class DnsCache
{
    /// <summary>Все имена кэша; пусто, если прочитать не вышло.</summary>
    public static IReadOnlyList<string> Names()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!DnsGetCacheDataTable(out var entry))
                return [];

            while (entry != IntPtr.Zero)
            {
                var record = Marshal.PtrToStructure<CacheEntry>(entry);

                if (record.Name != IntPtr.Zero && Marshal.PtrToStringUni(record.Name) is { Length: > 0 } name)
                    names.Add(name.TrimEnd('.'));

                var next = record.Next;

                DnsFree(record.Name, FreeFlat);
                DnsFree(entry, FreeFlat);

                entry = next;
            }
        }
        catch (Exception)
        {
            // Функция недокументированная: на другой сборке Windows её может
            // не оказаться, и тогда имён просто нет — пин работает как прежде.
        }

        return [.. names];
    }

    /// <summary>Имена, лежащие под зонами (зона сама — тоже).</summary>
    public static IReadOnlyList<string> Under(IEnumerable<string> names, IEnumerable<string> zones)
    {
        var bare = zones.Select(z => z.TrimStart('*', '.').ToLowerInvariant()).Where(z => z.Length > 0).ToList();

        return names
            .Select(n => n.ToLowerInvariant())
            .Where(n => bare.Any(z => n == z || n.EndsWith("." + z, StringComparison.Ordinal)))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    private const int FreeFlat = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CacheEntry
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable", SetLastError = true)]
    private static extern bool DnsGetCacheDataTable(out IntPtr table);

    [DllImport("dnsapi.dll", EntryPoint = "DnsFree")]
    private static extern void DnsFree(IntPtr data, int type);
}
