using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NetZapret.Core.Diagnostics;

/// <summary>Браузер, который резолвит имена сам, мимо системы.</summary>
/// <param name="Browser">Имя браузера для человека.</param>
/// <param name="Provider">Куда он ходит за именами, если это видно; иначе <c>null</c>.</param>
/// <param name="Setting">Где это выключается — словами из самого браузера.</param>
public sealed record BrowserDnsBypass(string Browser, string? Provider, string Setting);

/// <summary>
/// Находит браузеры со своим шифрованным DNS.
/// </summary>
/// <remarks>
/// <para>
/// Такой браузер не спрашивает системный резолвер, а с ним обходит и файл
/// hosts, и наш туннель: пины не действуют, fakeip не выдаётся, и маршрут
/// «через VPN» для имени не срабатывает. Со стороны это неотличимо от
/// «программа не работает» (вики Zapret GUI, статья о hosts, 2026).
/// </para>
/// <para>
/// Режим Chromium «автоматически» (умолчание) не в счёт: он переходит на DoH
/// только того резолвера, что стоит в системе, а такой DoH из туннеля
/// отвергается нашим правилом (SingBoxConfigCompiler.BuildDohRejects), и
/// браузер откатывается на обычный запрос. Опасен режим «безопасный» —
/// со своим провайдером, — и DoH в Firefox.
/// </para>
/// <para>
/// Только чтение: файлы настроек браузеров и ветки политик реестра.
/// Выключать за человека нельзя — это его браузер.
/// </para>
/// </remarks>
public static class BrowserDns
{
    private static readonly (string Name, string Folder, string PolicyKey)[] Chromium =
    [
        ("Chrome", @"Google\Chrome\User Data", @"SOFTWARE\Policies\Google\Chrome"),
        ("Edge", @"Microsoft\Edge\User Data", @"SOFTWARE\Policies\Microsoft\Edge"),
        ("Яндекс Браузер", @"Yandex\YandexBrowser\User Data", @"SOFTWARE\Policies\YandexBrowser"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data", @"SOFTWARE\Policies\BraveSoftware\Brave"),
        ("Vivaldi", @"Vivaldi\User Data", @"SOFTWARE\Policies\Vivaldi"),
        ("Opera", @"Opera Software\Opera Stable", @"SOFTWARE\Policies\Opera Software\Opera"),
    ];

    /// <summary>Браузеры, у которых сейчас включён свой DNS.</summary>
    public static IReadOnlyList<BrowserDnsBypass> Scan()
    {
        var found = new List<BrowserDnsBypass>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var (name, folder, policy) in Chromium)
        {
            // Opera держит Local State в Roaming, остальные — в Local.
            var state = new[] { Path.Combine(local, folder, "Local State"), Path.Combine(roaming, folder, "Local State") }
                .FirstOrDefault(File.Exists);

            if (FromPolicy(policy) is { } forced)
            {
                if (forced.Mode == "secure")
                    found.Add(new(name, forced.Template, "включено политикой организации — выключает администратор"));

                continue;
            }

            if (state is not null && ChromiumSecure(SafeRead(state)) is { } template)
                found.Add(new(name, template, "Настройки → Конфиденциальность и безопасность → Использовать безопасный DNS"));
        }

        var profiles = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");

        if (Directory.Exists(profiles))
        {
            foreach (var prefs in Directory.EnumerateFiles(profiles, "prefs.js", SearchOption.AllDirectories))
            {
                if (FirefoxTrr(SafeRead(prefs)) is { } trr)
                {
                    found.Add(new("Firefox", trr, "Настройки → Приватность и защита → DNS через HTTPS"));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>Шаблон провайдера, если в Local State включён режим «безопасный».</summary>
    /// <returns><c>null</c> — режим не «безопасный»; пустая строка — включён, провайдер не записан.</returns>
    internal static string? ChromiumSecure(string? localState)
    {
        if (string.IsNullOrWhiteSpace(localState))
            return null;

        try
        {
            using var document = JsonDocument.Parse(localState);

            if (!document.RootElement.TryGetProperty("dns_over_https", out var doh)
                || !doh.TryGetProperty("mode", out var mode)
                || mode.GetString() != "secure")
            {
                return null;
            }

            return doh.TryGetProperty("templates", out var templates) ? templates.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Адрес DoH Firefox, если TRR включён (режим 2 — первым, 3 — только он).</summary>
    internal static string? FirefoxTrr(string? prefs)
    {
        if (string.IsNullOrEmpty(prefs))
            return null;

        var mode = Regex.Match(prefs, @"user_pref\(""network\.trr\.mode"",\s*(\d+)\)");

        if (!mode.Success || mode.Groups[1].Value is not ("2" or "3"))
            return null;

        var uri = Regex.Match(prefs, @"user_pref\(""network\.trr\.uri"",\s*""([^""]*)""\)");

        return uri.Success ? uri.Groups[1].Value : "";
    }

    private static (string Mode, string? Template)? FromPolicy(string key)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var policy = hive.OpenSubKey(key);

                if (policy?.GetValue("DnsOverHttpsMode") is string mode)
                    return (mode, policy.GetValue("DnsOverHttpsTemplates") as string);
            }
            catch (Exception)
            {
                // Ветка закрыта правами — политики, значит, не видно, и судим по файлу.
            }
        }

        return null;
    }

    /// <summary>
    /// Читает файл, который браузер может держать открытым.
    /// </summary>
    private static string? SafeRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
