using System.Diagnostics;

namespace NetZapret.Subscriptions;

/// <summary>Пара ключей WireGuard в base64.</summary>
public sealed record WireGuardKeyPair(string PrivateKey, string PublicKey);

/// <summary>
/// Заводит пару ключей WireGuard руками движка.
/// </summary>
/// <remarks>
/// <para>
/// Своей реализации нет намеренно. Ключи WireGuard — это X25519, а в .NET 8
/// этой кривой нет: <c>System.Security.Cryptography</c> её не знает, CNG
/// в Windows тоже. Оставалось либо тянуть криптографическую библиотеку
/// в зависимости, либо писать арифметику поля самим — ради двух строк, которые
/// движок, уже лежащий в поставке, печатает по первой просьбе.
/// </para>
/// <para>
/// Путь к движку передаётся снаружи: искать его умеют обе половины программы
/// по-своему, и заводить здесь третий способ значило бы разойтись с ними
/// в первый же раз, когда поиск поменяется.
/// </para>
/// </remarks>
public static class WireGuardKeys
{
    /// <exception cref="InvalidOperationException">Если движок не отозвался ключами.</exception>
    public static WireGuardKeyPair Generate(string singBoxPath)
    {
        var start = new ProcessStartInfo(singBoxPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add("--disable-color");
        start.ArgumentList.Add("generate");
        start.ArgumentList.Add("wg-keypair");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("движок не запустился");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);

        var pair = Parse(output);

        return pair ?? throw new InvalidOperationException(
            "движок не выдал пару ключей — возможно, сборка без поддержки WireGuard");
    }

    /// <summary>Разбирает вывод <c>sing-box generate wg-keypair</c>.</summary>
    internal static WireGuardKeyPair? Parse(string output)
    {
        string? Field(string name) => output
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?[(name.Length + 1)..]
            .Trim();

        var privateKey = Field("PrivateKey");
        var publicKey = Field("PublicKey");

        return string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(publicKey)
            ? null
            : new WireGuardKeyPair(privateKey, publicKey);
    }
}
