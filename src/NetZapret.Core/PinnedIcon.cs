using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace NetZapret.Core;

/// <summary>
/// Значок закреплённой на панели задач кнопки — в цветах темы.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 21.10: «можно ли всё-таки пофиксить проблему, что при закреплении
/// иконка меняется на дефолтную?» Пока программа не закреплена, кнопка берёт
/// значок окна — он в цветах темы. Закреплённая берёт значок своего ярлыка,
/// а ярлык Windows делает из exe, где вшит значок темы по умолчанию.
/// </para>
/// <para>
/// Ярлыки закреплённых кнопок — обычные файлы .lnk в профиле
/// (<see cref="PinnedFolder"/>), и значок ярлыку можно указать свой. Значок
/// темы пишется файлом рядом с программой, и ярлыки, ведущие на наш exe,
/// смотрят на него. Имя файла — по его содержимому: сменилась тема — новый
/// файл, и проводник не покажет старую картинку из своего кеша по тому же
/// пути. Чужие ярлыки и реестр не трогаются.
/// </para>
/// </remarks>
public static class PinnedIcon
{
    /// <summary>Папка ярлыков, закреплённых на панели задач, у текущего пользователя.</summary>
    public static string PinnedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    private const string Prefix = "taskbar-";

    /// <summary>
    /// Ставит значок <paramref name="ico"/> закреплённым ярлыкам программы <paramref name="exe"/>.
    /// </summary>
    /// <returns>Сколько ярлыков поправлено; 0 — нечего (не закреплена или значок уже тот).</returns>
    /// <param name="iconFolder">Куда положить файл значка — рядом с программой (runtime).</param>
    /// <param name="pinnedFolder">Папка ярлыков; по умолчанию <see cref="PinnedFolder"/> — тесты подставляют свою.</param>
    /// <param name="notify">Известить оболочку; тесты — нет: проводник перерисовывал бы значки на каждом прогоне.</param>
    public static int Apply(byte[] ico, string exe, string iconFolder, string? pinnedFolder = null, bool notify = true)
    {
        var folder = pinnedFolder ?? PinnedFolder;

        if (!Directory.Exists(folder))
            return 0;

        var target = Path.GetFullPath(exe);
        var pins = Directory.EnumerateFiles(folder, "*.lnk")
            .Where(link => PointsTo(link, target))
            .ToList();

        // Не закреплена — файла значка не заводим вовсе.
        if (pins.Count == 0)
            return 0;

        var icon = Path.GetFullPath(Path.Combine(iconFolder,
            Prefix + Convert.ToHexString(SHA256.HashData(ico))[..12].ToLowerInvariant() + ".ico"));

        if (!File.Exists(icon))
        {
            Directory.CreateDirectory(iconFolder);
            File.WriteAllBytes(icon, ico);
        }

        int changed = 0;

        foreach (var link in pins)
        {
            try
            {
                if (SetIcon(link, icon))
                {
                    changed++;
                    if (notify)
                        SHChangeNotify(UpdateItem, ByPath, link, IntPtr.Zero);
                }
            }
            catch (Exception)
            {
                // Ярлык занят или не читается — его значок останется прежним.
            }
        }

        // Прежние значки — только после того, как ярлыки смотрят на новый.
        foreach (var old in Directory.EnumerateFiles(iconFolder, Prefix + "*.ico"))
        {
            if (!string.Equals(Path.GetFullPath(old), icon, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception)
                {
                    // Занят проводником — уберётся в следующий раз.
                }
            }
        }

        if (changed > 0 && notify)
            SHChangeNotify(AssociationsChanged, 0, null, IntPtr.Zero);

        return changed;
    }

    /// <summary>Ведёт ли ярлык на <paramref name="target"/>.</summary>
    private static bool PointsTo(string link, string target)
    {
        try
        {
            var shortcut = Load(link);
            var path = new StringBuilder(1024);
            shortcut.GetPath(path, path.Capacity, IntPtr.Zero, 0);

            return path.Length > 0
                && string.Equals(Path.GetFullPath(path.ToString()), target, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Ставит ярлыку значок; <c>false</c> — он уже этот.</summary>
    private static bool SetIcon(string link, string icon)
    {
        var shortcut = Load(link);
        var current = new StringBuilder(1024);
        shortcut.GetIconLocation(current, current.Capacity, out int index);

        if (index == 0 && string.Equals(current.ToString(), icon, StringComparison.OrdinalIgnoreCase))
            return false;

        shortcut.SetIconLocation(icon, 0);
        ((IPersistFile)shortcut).Save(link, true);
        return true;
    }

    private static IShellLinkW Load(string link)
    {
        var shortcut = (IShellLinkW)new ShellLink();
        ((IPersistFile)shortcut).Load(link, 0);
        return shortcut;
    }

    /// <summary>Ярлык для тестов: <paramref name="link"/> ведёт на <paramref name="target"/>.</summary>
    public static void CreateShortcut(string link, string target)
    {
        var shortcut = (IShellLinkW)new ShellLink();
        shortcut.SetPath(target);
        ((IPersistFile)shortcut).Save(link, true);
    }

    /// <summary>Значок ярлыка — путь и номер.</summary>
    public static (string Path, int Index) IconOf(string link)
    {
        var shortcut = Load(link);
        var current = new StringBuilder(1024);
        shortcut.GetIconLocation(current, current.Capacity, out int index);
        return (current.ToString(), index);
    }

    private const int UpdateItem = 0x00002000;
    private const int AssociationsChanged = 0x08000000;
    private const uint ByPath = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
