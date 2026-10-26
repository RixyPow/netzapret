package org.netzapret.android;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.os.Build;

import org.json.JSONObject;

import java.io.File;

import go.Seq;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.SetupOptions;

/**
 * Libbox.setup — один раз на процесс, до CommandServer и checkConfig
 * (libbox.md §4.1): там глобальные пути Go. Пути — в папке приложения:
 *   base     files/runtime/libbox  — command.sock и снимок конфига (configuration.json:
 *                                     в нём ключи серверов, поэтому только внутренняя папка);
 *   working  из запроса (по умолчанию тот же base) — cache.db и отчёты о падениях;
 *                                     относительные пути конфига Go считает от неё;
 *   temp     files/runtime/libbox-temp — всё движка в одной папке runtime, чистится вместе с ней.
 */
final class NzLibbox {
    private NzLibbox() {
    }

    private static boolean ready;
    private static String workingPath;

    /**
     * {@code requestedWorkingPath} null — годится любая уже сделанная настройка
     * (checkConfig не должен сдвигать пути под работающим движком).
     */
    static synchronized void ensure(Context context, String requestedWorkingPath) throws Exception {
        if (ready && requestedWorkingPath == null) {
            return;
        }
        Context app = context.getApplicationContext() != null ? context.getApplicationContext() : context;
        File base = new File(new File(app.getFilesDir(), "runtime"), "libbox");
        String working = requestedWorkingPath != null && !requestedWorkingPath.isEmpty()
                ? requestedWorkingPath : base.getAbsolutePath();
        if (ready && working.equals(workingPath)) {
            return;
        }
        // Go создаёт working и temp сам, а base — нет, а в нём слушает command.sock.
        base.mkdirs();
        new File(working).mkdirs();
        File temp = new File(new File(app.getFilesDir(), "runtime"), "libbox-temp");
        temp.mkdirs();

        if (!ready) {
            // RunOnJvm gomobile берёт контекст отсюда.
            Seq.setContext(app);
        }

        boolean debuggable = (app.getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0;
        int sdk = Build.VERSION.SDK_INT;
        SetupOptions o = new SetupOptions();
        o.setBasePath(base.getAbsolutePath());
        o.setWorkingPath(working);
        o.setTempPath(temp.getAbsolutePath());
        // golang/go#68760 («stack split at bad time»): у SFA включено на 24–25 и 28+.
        o.setFixAndroidStack(debuggable || sdk <= 25 || sdk >= 28);
        // 0 — сервер команд на Unix-сокете в папке приложения: без слушателя на 127.0.0.1 (DESIGN.md).
        o.setCommandServerListenPort(0);
        o.setCommandServerSecret("");
        o.setLogMaxLines(1000);
        o.setDebug(debuggable);
        o.setCrashReportSource("NetZapret");
        String[] version = appVersion(app);
        o.setAppVersion(version[0]);
        o.setAppMarketingVersion(version[1]);
        // Убийца по памяти и отчёт о питании — для iOS и отладки SFA; нам не нужны.
        o.setOomKillerEnabled(false);
        o.setOomKillerDisabled(true);
        o.setOomMemoryLimit(0);
        o.setPowerReportEnabled(false);
        JSONObject meta = new JSONObject();
        meta.put("os", "Android " + Build.VERSION.RELEASE);
        meta.put("sdk", sdk);
        meta.put("manufacturer", Build.MANUFACTURER);
        meta.put("model", Build.MODEL);
        o.setPlatformMetadata(meta.toString());
        Libbox.setup(o);
        ready = true;
        workingPath = working;
    }

    private static String[] appVersion(Context app) {
        try {
            PackageInfo info = app.getPackageManager().getPackageInfo(app.getPackageName(), 0);
            long code = Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode;
            return new String[]{Long.toString(code), info.versionName == null ? "" : info.versionName};
        } catch (Throwable e) {
            return new String[]{"0", ""};
        }
    }
}
