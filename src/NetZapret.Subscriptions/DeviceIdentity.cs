using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NetZapret.Subscriptions;

/// <summary>
/// Кто спрашивает подписку: постоянный номер устройства и его описание —
/// то, что панели с привязкой к устройству (HWID) требуют в заголовках.
/// </summary>
/// <remarks>
/// <para>
/// Запрос из чата 23.09: подписка «с HWID» у нас не читается. Сам sing-box
/// тут ни при чём — подписку качает программа, а панель (обычно Remnawave)
/// раздаёт её только устройствам, назвавшим себя заголовком <c>x-hwid</c>,
/// как это делают Happ и v2rayTun. Без него — пустой список или отказ.
/// </para>
/// <para>
/// Номер обязан быть постоянным: меняйся он между запусками, панель
/// считала бы каждый запуск новым устройством и упёрлась бы в лимит.
/// Берётся из <c>MachineGuid</c> Windows — он живёт до переустановки
/// системы, — но не сам, а его отпечаток с солью программы: панели незачем
/// знать настоящий идентификатор машины, и по отпечатку его не восстановить.
/// Реестр только читается.
/// </para>
/// </remarks>
public static class DeviceIdentity
{
    private static readonly Lazy<string> Hwid = new(Compute);

    /// <summary>Постоянный номер этого устройства: 32 шестнадцатеричных знака.</summary>
    public static string Id => Hwid.Value;

    /// <summary>Заголовки, которыми клиенты с HWID называют себя панели.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Headers() =>
    [
        new("x-hwid", Id),
        new("x-device-os", "Windows"),
        new("x-ver-os", Environment.OSVersion.Version.ToString()),
        new("x-device-model", Model()),
    ];

    /// <summary>Отпечаток из идентификатора Windows; запасной — из имени машины.</summary>
    internal static string FromSeed(string seed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("NetZapret HWID:" + seed)))[..32]
            .ToLowerInvariant();

    private static string Compute()
    {
        // Без MachineGuid (урезанная система, запрет на чтение) — имя машины:
        // тоже постоянное, и тоже уходит только отпечатком.
        var seed = Read(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid") ?? Environment.MachineName;

        return FromSeed(seed);
    }

    /// <summary>Модель компьютера из описания BIOS; неизвестна — «Windows PC».</summary>
    private static string Model()
    {
        var model = Read(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName")?.Trim();

        // Заголовок HTTP — только печатные ASCII: модель с чем-то иным
        // уронила бы весь запрос подписки, а не одно поле.
        return string.IsNullOrEmpty(model) || model.Any(c => c < 0x20 || c > 0x7E)
            ? "Windows PC"
            : model;
    }

    private static string? Read(string key, string name)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var sub = hive.OpenSubKey(key);

            return sub?.GetValue(name) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
