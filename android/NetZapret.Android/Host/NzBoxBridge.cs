using Android.Content;
using Android.Runtime;

namespace NetZapret.Android.Host;

/// <summary>
/// Мост к Java-классу <c>org.netzapret.android.NzBox</c> (WP3): статические
/// методы с простыми типами, JSON строками в обе стороны (DESIGN.md §4).
/// </summary>
/// <remarks>
/// <para>
/// Сырой JNI, а не привязка: привязка libbox валит мост сборщика мусора
/// Mono.Android при запуске (DESIGN.md, образец VPNRouter), и весь код,
/// который трогает libbox, живёт в Java. Сюда приходит только NzBox — шесть
/// методов, и для них хватает FindClass и CallStatic*.
/// </para>
/// <para>
/// Класс ищется один раз, из главного потока (<see cref="Initialize"/> —
/// в MainApplication.OnCreate): в потоке, подключённом к JVM из натива,
/// FindClass видит только системный загрузчик классов и наш класс не нашёл бы.
/// Глобальная ссылка потом годится в любом потоке.
/// </para>
/// <para>
/// Секреты (пароль ByeDPI в запросе) мост не печатает: исключение Java
/// отдаётся вызывающему текстом самой Java, а запрос в текст не попадает.
/// </para>
/// </remarks>
internal static class NzBoxBridge
{
    private const string ClassName = "org/netzapret/android/NzBox";

    private static readonly object Sync = new();
    private static IntPtr _class;
    private static IntPtr _start, _stop, _status, _command, _checkConfig, _version;
    private static string? _missing;

    /// <summary>Найти класс и методы; без Java-части — запомнить, почему моста нет.</summary>
    public static void Initialize()
    {
        lock (Sync)
        {
            if (_class != IntPtr.Zero || _missing is not null)
                return;

            try
            {
                // Mono.Android отдаёт глобальную ссылку: она живёт, пока её не удалят.
                var found = JNIEnv.FindClass(ClassName);

                _start = JNIEnv.GetStaticMethodID(found, "start", "(Landroid/content/Context;Ljava/lang/String;)V");
                _stop = JNIEnv.GetStaticMethodID(found, "stop", "(Landroid/content/Context;)V");
                _status = JNIEnv.GetStaticMethodID(found, "status", "()Ljava/lang/String;");
                _command = JNIEnv.GetStaticMethodID(found, "command", "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
                _checkConfig = JNIEnv.GetStaticMethodID(found, "checkConfig", "(Ljava/lang/String;)Ljava/lang/String;");
                _version = JNIEnv.GetStaticMethodID(found, "version", "()Ljava/lang/String;");
                _class = found;
            }
            catch (Java.Lang.Throwable error)
            {
                // Сборка с -p:NzNoJava=true или Java-часть без нужного метода.
                _missing = $"нет моста к движку ({error.GetType().Name}: {error.Message}) — APK собран без Java-части NzBox";
            }
        }
    }

    /// <summary>Есть ли мост; <paramref name="why"/> — почему нет.</summary>
    public static bool Available(out string? why)
    {
        Initialize();
        why = _missing;
        return _class != IntPtr.Zero;
    }

    public static void Start(Context context, string requestJson)
    {
        Require();
        var json = JNIEnv.NewString(requestJson);

        try
        {
            JNIEnv.CallStaticVoidMethod(_class, _start, new JValue(context), new JValue(json));
        }
        finally
        {
            JNIEnv.DeleteLocalRef(json);
        }
    }

    public static void Stop(Context context)
    {
        Require();
        JNIEnv.CallStaticVoidMethod(_class, _stop, new JValue(context));
    }

    /// <summary>Состояние движков JSON-ом; без моста — «failed» с причиной.</summary>
    public static string Status()
    {
        if (!Available(out var why))
            return Failed(why);

        return StringResult(JNIEnv.CallStaticObjectMethod(_class, _status)) ?? "{}";
    }

    public static string Command(string name, string argJson)
    {
        if (!Available(out var why))
            return ErrorJson(why);

        var jName = JNIEnv.NewString(name);
        var jArg = JNIEnv.NewString(argJson);

        try
        {
            return StringResult(JNIEnv.CallStaticObjectMethod(_class, _command, new JValue(jName), new JValue(jArg)))
                ?? ErrorJson("движок не ответил");
        }
        finally
        {
            JNIEnv.DeleteLocalRef(jName);
            JNIEnv.DeleteLocalRef(jArg);
        }
    }

    /// <summary>Проверка конфига libbox: пусто — годен, иначе текст ошибки.</summary>
    public static string CheckConfig(string configJson)
    {
        Require();
        var json = JNIEnv.NewString(configJson);

        try
        {
            return StringResult(JNIEnv.CallStaticObjectMethod(_class, _checkConfig, new JValue(json))) ?? "";
        }
        finally
        {
            JNIEnv.DeleteLocalRef(json);
        }
    }

    public static string? Version() =>
        Available(out _) ? StringResult(JNIEnv.CallStaticObjectMethod(_class, _version)) : null;

    private static void Require()
    {
        if (!Available(out var why))
            throw new InvalidOperationException(why);
    }

    private static string? StringResult(IntPtr handle) =>
        handle == IntPtr.Zero ? null : JNIEnv.GetString(handle, JniHandleOwnership.TransferLocalRef);

    private static string Failed(string? why) =>
        "{\"state\":\"failed\",\"error\":" + System.Text.Json.JsonSerializer.Serialize(why ?? "нет моста к движку") + "}";

    private static string ErrorJson(string? why) =>
        "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(why ?? "нет моста к движку") + "}";
}
