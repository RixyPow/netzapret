#if NZ_INTEGRATED
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Android.Content;
using NetZapret.Android.Host;
using NetZapret.Zapret;

namespace NetZapret.Android.Startup;

/// <summary>
/// Сохранённый запуск nfqws2 — к этому процессу и этой установке APK.
/// </summary>
/// <remarks>
/// <para>
/// Сценарий <c>runtime/nfqws2-start.sh</c> пишет запуск из приложения
/// (<c>EngineStart.PrepareRootAsync</c>), и в нём два значения, верных только
/// для того процесса и той установки: номер процесса приложения
/// (<c>( APP=…</c> — за ним следит сторож, и пропал процесс — снимает правила
/// и гасит nfqws2) и полный путь <c>libnfqws2.so</c> в nativeLibraryDir
/// (<c>exec '…'</c>). А «постоянный VPN» системы и плитка поднимают службу
/// из сохранённого запроса (<c>runtime/nz-request.json</c>) в новом процессе,
/// не спрашивая приложение, — с этим же сценарием.
/// </para>
/// <para>
/// Что выходило (найдено чтением 27.10, на телефоне не запускалось): после
/// обновления APK у пакета новая папка <c>/data/app/~~…/</c>, система
/// поднимает постоянный VPN заново, <c>exec</c> старого пути не находит файла
/// и падает с кодом 127 — уже после строки <c>NZ_PID</c>, так что root
/// числится работающим, правила с <c>--queue-bypass</c> остаются, а десинка
/// нет. После перезагрузки или смерти процесса и без обновления: номер
/// процесса в сценарии чужой, сторож за 3 с решает, что приложение умерло,
/// снимает правила и гасит nfqws2 — а состояние всё равно «работает».
/// </para>
/// <para>
/// Поэтому на каждом старте процесса (<see cref="AppStartup.Run"/> идёт
/// в <c>Application.onCreate</c>, раньше любой службы) оба значения
/// переписываются на нынешние. Правильнее отдавать их сценарию при запуске
/// аргументами из Java (NzRoot) — это правка Java и библиотеки
/// (<c>Nfqws2CommandLine.StartScript</c>); когда сценарий станет брать их
/// так, строк <c>( APP=</c> и <c>exec '…/libnfqws2.so'</c> в нём не будет,
/// и здесь ничего не тронется. Если же файла nfqws2 в этой установке нет
/// или сценарий не того вида — root из сохранённого запроса убирается:
/// честное «десинк не поднят» лучше, чем «работает» без десинка.
/// </para>
/// </remarks>
internal static class SavedRootStart
{
    private static readonly Regex AppLine = new(@"^\( APP=\d+$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // Строка exec продолжается аргументами через « \», поэтому без конца строки.
    private static readonly Regex ExecLine = new(@"^exec '[^']*/libnfqws2\.so'", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static void Rebind(Context context, string files)
    {
        var runtime = Path.Combine(files, "runtime");
        var script = Path.Combine(runtime, Nfqws2Scripts.StartName);

        try
        {
            if (!File.Exists(script))
                return;

            var text = File.ReadAllText(script);
            var apps = AppLine.Matches(text).Count;
            var execs = ExecLine.Matches(text).Count;

            if (apps == 0 && execs == 0)
                return;

            if (apps > 1 || execs > 1)
            {
                DropRoot(runtime, "сценарий nfqws2 не того вида");
                return;
            }

            var exe = Path.Combine(context.ApplicationInfo!.NativeLibraryDir!, "libnfqws2.so");

            if (execs == 1 && !File.Exists(exe))
            {
                DropRoot(runtime, "в этой установке нет libnfqws2.so");
                return;
            }

            var pid = global::Android.OS.Process.MyPid().ToString(CultureInfo.InvariantCulture);
            var updated = AppLine.Replace(text, _ => "( APP=" + pid, 1);
            updated = ExecLine.Replace(updated, _ => "exec " + Nfqws2CommandLine.Quote(exe), 1);

            if (updated != text)
                WriteReplacing(script, updated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            // Не переписался — со старыми значениями он не заработает (см. выше).
            DropRoot(runtime, $"сценарий nfqws2 не переписан ({error.GetType().Name})");
        }
    }

    /// <summary>
    /// Убрать root из сохранённого запроса: система и плитка поднимут VPN
    /// без nfqws2, а с root он встанет по «Запустить» в приложении.
    /// </summary>
    /// <remarks>
    /// В запросе пароль ByeDPI этой сессии: файл правится на месте, наружу
    /// и в журнал из него не уходит ничего.
    /// </remarks>
    private static void DropRoot(string runtime, string why)
    {
        var path = Path.Combine(runtime, "nz-request.json");

        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject request)
                return;

            if (request["root"] is null)
                return;

            request["root"] = null;
            WriteReplacing(path, request.ToJsonString());

            AndroidJournal.Write("Запуск", $"Сохранённый запуск без nfqws2: {why}. "
                + "Постоянный VPN и плитка поднимут его без десинка с root — нажмите «Запустить» в приложении.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Только тип: в тексте ошибки разбора мог бы оказаться кусок запроса с паролем.
            AndroidJournal.Write("Запуск", $"Сохранённый запрос не поправлен ({error.GetType().Name}): {why}.");
        }
    }

    /// <summary>Через временный файл: служба не прочтёт половину.</summary>
    private static void WriteReplacing(string path, string text)
    {
        var temporary = path + ".nz-new";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
#endif
