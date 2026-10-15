using System.Text.Json;
namespace NetZapret.Supervisor;

/// <summary>Надзор не перезапускает себя: окно/трей предлагают обычный перезапуск через EngineControl.</summary>
public sealed record SubscriptionRefreshNotice(string Id, int SupervisorProcessId)
{
    public static string DefaultPath => Path.Combine("runtime", "subscription-refresh.notice.json");
    public static void Write(int processId)
    {
        string path = DefaultPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new SubscriptionRefreshNotice(Guid.NewGuid().ToString("N"), processId)));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static SubscriptionRefreshNotice? Load()
    {
        try { return File.Exists(DefaultPath) ? JsonSerializer.Deserialize<SubscriptionRefreshNotice>(File.ReadAllText(DefaultPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
