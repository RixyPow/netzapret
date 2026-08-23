using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetZapret.Supervisor;

/// <summary>
/// Снимок состояния для команды <c>status</c>.
/// </summary>
/// <remarks>
/// Пишется на диск, потому что <c>start</c>, <c>status</c> и <c>stop</c> —
/// три отдельных запуска программы. Другого способа рассказать одному
/// процессу о состоянии другого без службы Windows или именованного канала нет,
/// а файл проще и переживает перезагрузку.
/// </remarks>
public sealed record SupervisorState
{
    public required int SupervisorProcessId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public required IReadOnlyList<ServiceState> Services { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine("runtime", "supervisor.state.json");

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(this with { UpdatedAt = DateTimeOffset.Now }, Options);

        // Запись через временный файл: status может читать состояние ровно
        // в момент обновления и получить обрезанный JSON.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public static SupervisorState? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SupervisorState>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Файл состояния — вспомогательный; невозможность удалить не критична.
        }
    }

    /// <summary>Жив ли процесс супервизора, записавший это состояние.</summary>
    public bool IsSupervisorAlive()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(SupervisorProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed record ServiceState
{
    public required string Name { get; init; }

    public required ServiceHealth Health { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public int RestartCount { get; init; }

    public string? LastError { get; init; }
}
