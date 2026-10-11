using System.Net;
using System.Net.Sockets;

namespace NetZapret.Core.Rules;

/// <summary>
/// Адрес или подсеть из того, что человек вписал: в поле «Маршрутов»
/// или строкой своего списка.
/// </summary>
/// <remarks>
/// До 12.10 поле «Маршрутов» знало одни имена, и «45.82.160.0/24» оно
/// принимало за имя, срезав маску. Так у владельца завёлся список R.E.P.O.
/// из пяти адресов при правиле по именам: адреса список имён не сравнивает,
/// и правило не совпадало ни с чем — молча.
/// </remarks>
public static class AddressInput
{
    /// <summary>
    /// Адрес как есть или подсеть от её начала («45.82.160.0/24»);
    /// <c>null</c> — не адрес.
    /// </summary>
    /// <remarks>
    /// IPv4 — только четырьмя числами: IPAddress.TryParse принимает и «45»,
    /// и «1.2» (как 0.0.0.45 и 1.0.0.2), а это опечатки, а не адреса.
    /// Подсеть /0 — весь интернет, это режим «Туннель», а не маршрут.
    /// </remarks>
    public static string? Normalize(string? text)
    {
        var raw = (text ?? string.Empty).Trim();

        if (raw.Length == 0 || raw.Any(char.IsWhiteSpace))
            return null;

        int slash = raw.IndexOf('/');
        var addressPart = slash < 0 ? raw : raw[..slash];

        if (!IPAddress.TryParse(addressPart, out var address))
            return null;

        bool v4 = address.AddressFamily == AddressFamily.InterNetwork;

        if (v4 && addressPart.Count(c => c == '.') != 3)
            return null;

        if (!v4 && !addressPart.Contains(':'))
            return null;

        if (slash < 0)
            return address.ToString();

        int bits = v4 ? 32 : 128;

        if (!int.TryParse(raw[(slash + 1)..], out var prefix) || prefix < 1 || prefix > bits)
            return null;

        if (prefix == bits)
            return address.ToString();

        var bytes = address.GetAddressBytes();

        for (int i = 0; i < bytes.Length; i++)
        {
            int keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return $"{new IPAddress(bytes)}/{prefix}";
    }

    /// <summary>Строки списка — порознь имена и адреса.</summary>
    public static (IReadOnlyList<string> Names, IReadOnlyList<string> Addresses) Split(IEnumerable<string> lines)
    {
        var names = new List<string>();
        var addresses = new List<string>();

        foreach (var line in lines)
        {
            if (Normalize(line) is { } address)
                addresses.Add(address);
            else
                names.Add(line);
        }

        return (names, addresses);
    }
}
