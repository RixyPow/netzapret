using System.Runtime.InteropServices;
using System.Text;

namespace NetZapret.Wfp;

/// <summary>
/// Переводит NT-пути из WFP appId в привычную DOS-нотацию.
/// </summary>
/// <remarks>
/// WFP отдаёт appId в виде <c>\device\harddiskvolume4\program files\...</c>.
/// Правила пользователь пишет в терминах <c>C:\Program Files\...</c> или просто
/// <c>chrome.exe</c>, поэтому путь нужно нормализовать до сопоставления.
/// Карта устройств строится один раз и обновляется, если встретился неизвестный
/// префикс (подключили диск, смонтировали образ).
/// </remarks>
public sealed class NtPathResolver
{
    private const string MupPrefix = @"\device\mup\";

    private readonly object _sync = new();
    private Dictionary<string, string> _deviceToDrive;

    public NtPathResolver()
    {
        _deviceToDrive = BuildDeviceMap();
    }

    /// <summary>
    /// Преобразует NT-путь в DOS-путь. Если сопоставить не удалось, возвращает
    /// исходную строку — она всё ещё пригодна для сопоставления по имени файла.
    /// </summary>
    public string? ToDosPath(string? ntPath)
    {
        if (string.IsNullOrWhiteSpace(ntPath))
            return null;

        var normalized = ntPath.Trim();

        // Некоторые слои отдают уже готовый путь либо форму \??\C:\...
        if (normalized.StartsWith(@"\??\", StringComparison.Ordinal))
            normalized = normalized[4..];

        if (normalized.Length >= 2 && normalized[1] == ':')
            return normalized;

        var lower = normalized.ToLowerInvariant();

        if (lower.StartsWith(MupPrefix, StringComparison.Ordinal))
            return @"\\" + normalized[MupPrefix.Length..];

        if (TryMap(lower, normalized, out var mapped))
            return mapped;

        // Неизвестный префикс — возможно, том появился уже после старта.
        RefreshDeviceMap();

        return TryMap(lower, normalized, out mapped) ? mapped : normalized;
    }

    private bool TryMap(string lowerPath, string originalPath, out string? result)
    {
        Dictionary<string, string> map;
        lock (_sync)
            map = _deviceToDrive;

        foreach (var (device, drive) in map)
        {
            if (!lowerPath.StartsWith(device, StringComparison.Ordinal))
                continue;

            // Требуем границу компонента пути, иначе \device\harddiskvolume1
            // совпадёт с началом \device\harddiskvolume11.
            if (lowerPath.Length > device.Length && lowerPath[device.Length] != '\\')
                continue;

            result = drive + originalPath[device.Length..];
            return true;
        }

        result = null;
        return false;
    }

    private void RefreshDeviceMap()
    {
        var fresh = BuildDeviceMap();
        lock (_sync)
            _deviceToDrive = fresh;
    }

    private static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var buffer = new StringBuilder(1024);

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            buffer.Clear();

            uint length = QueryDosDeviceW(drive, buffer, (uint)buffer.Capacity);
            if (length == 0)
                continue;

            var device = buffer.ToString();
            if (device.Length == 0)
                continue;

            map[device.ToLowerInvariant()] = drive;
        }

        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);
}
