using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetZapret.Core;

/// <summary>
/// Кеш значков Windows — перечитать, когда значок программы сменился.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 21.10: «добавь автоочистку кеша панели задач, чтоб иконки
/// по-новой подгружались». Значок exe вшит при сборке, а проводник
/// и панель задач держат его в своём кеше (iconcache): закреплённый ярлык
/// и меню «Пуск» после обновления показывали бы прежний значок, пока кеш
/// не устареет сам.
/// </para>
/// <para>
/// Без удаления файлов кеша и без перезапуска проводника — они рвут панель
/// задач на глазах. Два штатных хода: извещение оболочки «значки сменились»
/// (<c>SHChangeNotify</c>, <c>SHCNE_ASSOCCHANGED</c>) и <c>ie4uinit.exe -show</c> —
/// им Windows 10 и 11 перечитывают кеш значков пользователя; прав
/// администратора не нужно.
/// </para>
/// </remarks>
public static class IconCache
{
    private const int AssociationsChanged = 0x08000000;
    private const uint ByIdList = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// Перечитать кеш, если отпечаток значка (<paramref name="stamp"/>) не тот,
    /// что при прошлом запуске; отпечаток запоминается в настройках.
    /// </summary>
    /// <returns>Перечитывался ли кеш.</returns>
    /// <remarks>
    /// Один раз на смену значка, а не на каждом запуске: перечитывание кеша
    /// заставляет проводник заново рисовать все значки, и раз в запуск это
    /// было бы заметно. Первый запуск новой установки — тоже смена.
    /// </remarks>
    /// <param name="refresh">Само перечитывание; по умолчанию <see cref="Refresh"/> — тесты подставляют своё.</param>
    public static bool RefreshIfChanged(string stamp, string settingsPath, Action? refresh = null)
    {
        var settings = AppSettings.Load(settingsPath);

        if (settings.IconStamp == stamp)
            return false;

        (settings with { IconStamp = stamp }).Save(settingsPath);

        // В фоне: ie4uinit идёт до секунды, а окно ждать его незачем.
        _ = Task.Run(refresh ?? Refresh);
        return true;
    }

    /// <summary>Просит Windows перечитать значки. Сбой не страшен — значок обновится сам, позже.</summary>
    public static void Refresh()
    {
        try
        {
            SHChangeNotify(AssociationsChanged, ByIdList, IntPtr.Zero, IntPtr.Zero);

            var tool = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");

            if (!File.Exists(tool))
                return;

            using var process = Process.Start(new ProcessStartInfo(tool, "-show")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            process?.WaitForExit(10_000);
        }
        catch (Exception)
        {
            // Кеш значков — удобство: не перечитался, значок сменится, когда
            // Windows сама сочтёт кеш устаревшим.
        }
    }
}
