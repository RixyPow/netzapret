using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetZapret.Core;

/// <summary>Окно и фоновый надзор должны одинаково понимать настройки восстановления.</summary>
public static class RecoverySettingsFile
{
    public const string DefaultPath = "config/subscription-recovery.json";
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static RecoveryOptions Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return new();
        var options = JsonSerializer.Deserialize<RecoveryOptions>(File.ReadAllText(path), ReadOptions)
            ?? throw new JsonException("Recovery settings must be a JSON object.");
        options.Validate();
        return options;
    }

    /// <summary>Пустое поле возвращает 300 мс, но ошибочный ввод не должен молча менять порог.</summary>
    public static bool TryParseLatency(string? text, out int milliseconds)
    {
        milliseconds = 300;
        return string.IsNullOrWhiteSpace(text)
            || (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds)
                && milliseconds is >= 1 and <= 60000);
    }

    /// <summary>Два элемента интерфейса не должны затирать расширенные или будущие поля файла.</summary>
    public static void SaveUi(bool enabled, int milliseconds, string path = DefaultPath)
    {
        var current = Load(path);
        var next = current with { Enabled = enabled, MaxLatencyMs = milliseconds };
        next.Validate();
        var json = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new JsonException("Recovery settings must be a JSON object.")
            : JsonSerializer.SerializeToNode(current)!.AsObject();
        Set("Enabled", JsonValue.Create(enabled)!);
        Set("MaxLatencyMs", JsonValue.Create(milliseconds)!);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, full, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        void Set(string name, JsonNode value)
        {
            foreach (var key in json.Select(kv => kv.Key)
                .Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToArray())
                json.Remove(key);
            json[name] = value;
        }
    }
}
