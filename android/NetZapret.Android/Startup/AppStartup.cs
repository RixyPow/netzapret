using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.OS;
using Android.Provider;
using NetZapret.Core;
using NetZapret.Subscriptions;

namespace NetZapret.Android.Startup;

/// <summary>
/// Что делается до первого обращения к библиотекам src/ — в этом порядке
/// (portability.md §8, п. 6).
/// </summary>
/// <remarks>
/// Порядок не прихоть: <c>ZapretPaths.PresetDirectory</c> читает
/// <c>AppPaths.ProgramDirectory</c> один раз, статическим полем, а пути
/// config/ и runtime/ берутся от текущей папки — у процесса Android это «/».
/// Кто тронет библиотеку раньше, получит пути корня файловой системы.
/// </remarks>
internal static class AppStartup
{
    private static bool _done;

    /// <summary>
    /// Почему поставка не распаковалась; null — распаковалась.
    /// </summary>
    /// <remarks>
    /// Падать из OnCreate приложения нельзя: процесс поднимают и служба,
    /// и плитка, и телефон ушёл бы в круг падений без единого слова. Окно
    /// открывается, а «Запустить» отвечает этой причиной.
    /// </remarks>
    public static string? PayloadError { get; private set; }

    public static void Run(Context context)
    {
        if (_done)
            return;

        var files = context.FilesDir!.AbsolutePath;

        // 1. Поставка — до всего: дальше её читают.
        try
        {
            Payload.Install(context, files);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Java.Lang.Exception)
        {
            // Тип и текст: секретов в поставке нет (личное в неё не входит).
            PayloadError = $"Не удалось распаковать файлы программы ({error.GetType().Name}: {error.Message}). "
                + "Освободите место на телефоне и перезапустите NetZapret.";
        }

        // 2–3. Папка программы и текущая папка — та, куда распаковано.
        AppPaths.ProgramDirectory = files;
        Directory.SetCurrentDirectory(files);

        // 4. Устройство для панелей подписок с HWID.
        var description = Describe(context, files);
        DeviceIdentity.Describe = () => description;

        // 5. Физическая сеть. Сокеты проб привязывать незачем: наш пакет
        //    исключён из своего VPN (exclude_package), и его соединения и так
        //    идут мимо TUN. IP_UNICAST_IF, которым это делает Windows, на Linux
        //    бросил бы SocketException (portability.md S4).
        PhysicalNetworkWatch.Start(context);
        PhysicalNetwork.InterfaceSource = PhysicalNetworkWatch.InterfaceIndex;
        PhysicalNetwork.SocketBinder = static (_, _) => { };

        _done = true;
    }

    /// <summary>
    /// Номер и описание телефона для заголовков x-hwid, x-device-os, x-ver-os,
    /// x-device-model (portability.md S2).
    /// </summary>
    /// <remarks>
    /// По умолчанию библиотека взяла бы имя машины, а оно на Android почти
    /// у всех «localhost»: панель с лимитом устройств видела бы все телефоны
    /// одним. ANDROID_ID свой у каждого приложения и пользователя; наружу
    /// уходит только его солёный SHA-256 (DeviceIdentity.FromSeed). Если
    /// система его не дала — случайный номер, один раз и навсегда, в runtime/.
    /// </remarks>
    private static DeviceDescription Describe(Context context, string files)
    {
        string? seed = null;

        try
        {
            seed = Settings.Secure.GetString(context.ContentResolver, Settings.Secure.AndroidId);
        }
        catch (Java.Lang.Exception)
        {
        }

        if (string.IsNullOrWhiteSpace(seed))
            seed = StoredSeed(files);

        return new DeviceDescription(seed, "Android", Build.VERSION.Release ?? "", Ascii(Build.Model) ?? "Android");
    }

    private static string StoredSeed(string files)
    {
        var path = Path.Combine(files, "runtime", "device-seed");

        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } stored)
                return stored;
        }
        catch (IOException)
        {
        }

        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, fresh);
        return fresh;
    }

    /// <summary>
    /// Только печатное ASCII: HttpClient отказывается слать заголовок
    /// с другими символами, а модели бывают и с «™», и по-китайски.
    /// </summary>
    internal static string? Ascii(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (c is >= ' ' and <= '~')
                builder.Append(c);
        }

        var text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text;
    }
}
