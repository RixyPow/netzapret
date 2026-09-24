using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>
/// Каким именем назваться панели этой подписки — если не тем, что по умолчанию.
/// </summary>
/// <remarks>
/// <para>
/// Владелец, 24.09: «если подписка заработала, когда мы назвались Happ, пусть
/// и дальше при чтении называется Happ». Без памяти каждое чтение такой
/// подписки начиналось с заведомого 404 под именем sing-box — лишний запрос
/// и лишняя секунда на каждое обновление серверов.
/// </para>
/// <para>
/// Ключ — отпечаток ссылки, а не она сама: ссылка на подписку равносильна
/// паролю, и второй её копии в ещё одном файле быть не должно. Файл лежит
/// в runtime\ — вне репозитория и архива, обновление его не трогает.
/// </para>
/// </remarks>
public static class AgentMemory
{
    public static string DefaultPath => Path.Combine("runtime", "subscription-agents.json");

    /// <summary>Отпечаток ссылки: по нему её не восстановить.</summary>
    internal static string Key(Uri url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("NetZapret subscription:" + url.AbsoluteUri)))[..32]
            .ToLowerInvariant();

    /// <summary>Запомненное имя; <c>null</c> — спрашивать как обычно.</summary>
    public static string? Get(Uri url, string? path = null)
    {
        var all = Read(path ?? DefaultPath);

        return all.TryGetValue(Key(url), out var agent) ? agent : null;
    }

    /// <summary>Запоминает имя; <c>null</c> — забыть, спрашивать как обычно.</summary>
    public static void Set(Uri url, string? agent, string? path = null)
    {
        var file = path ?? DefaultPath;
        var all = Read(file);
        var key = Key(url);

        if (agent is null ? !all.Remove(key) : all.TryGetValue(key, out var was) && was == agent)
            return;

        if (agent is not null)
            all[key] = agent;

        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(file, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Не записалось — в следующий раз спросим как обычно и снова
            // найдём рабочее имя. Отказ здесь не повод ронять чтение подписки.
        }
    }

    private static Dictionary<string, string> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
