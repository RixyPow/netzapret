using System.Text.Json;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Результат выбора DNS при сборке, а не повторный разбор большого singbox.json в окне.</summary>
public sealed record RouterDnsBuildState(string? RequestedRouterAddress, string? Warning)
{
    private sealed record Cached(DateTime WrittenUtc, long Length, RouterDnsBuildState? State);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string PathFor(string configPath) => configPath + ".router-dns-state.json";

    public static void Save(string configPath, AppSettings requested, string? warning)
    {
        var state = new RouterDnsBuildState(requested.UseRouterDns ? requested.RouterDnsAddress : null, warning);
        string path = System.IO.Path.GetFullPath(PathFor(configPath));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        lock (Gate)
        {
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(state));
                File.Move(temp, path, overwrite: true);
                var file = new FileInfo(path);
                Cache[path] = new(file.LastWriteTimeUtc, file.Length, state);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static RouterDnsBuildState? Load(string configPath)
    {
        string path = System.IO.Path.GetFullPath(PathFor(configPath));
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) { Cache.Remove(path); return null; }
                // Файл содержит только адрес и предупреждение. Повреждённый
                // мегабайтный sidecar тоже не должен зависнуть в потоке окна.
                if (file.Length > 8192) { Cache.Remove(path); return null; }
                if (Cache.TryGetValue(path, out var kept) && kept.WrittenUtc == file.LastWriteTimeUtc && kept.Length == file.Length)
                    return kept.State;
                var state = JsonSerializer.Deserialize<RouterDnsBuildState>(File.ReadAllText(path));
                Cache[path] = new(file.LastWriteTimeUtc, file.Length, state);
                return state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Cache.Remove(path); return null; }
        }
    }
}
