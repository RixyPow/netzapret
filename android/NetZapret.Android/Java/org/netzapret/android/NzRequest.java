package org.netzapret.android;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.Charset;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Запрос на запуск от C# (DESIGN.md §4). Лежит файлом
 * {@code <files>/runtime/nz-request.json}: службу поднимают и без C# —
 * постоянный VPN системы и плитка в шторке, — и тогда она берёт последний
 * запрос отсюда. В файле пароль ByeDPI этой сессии; он в папке приложения
 * рядом с конфигом, где и так лежат ключи серверов, и наружу не уходит.
 */
final class NzRequest {
    static final Charset UTF8 = Charset.forName("UTF-8");

    static final class ByeDpi {
        final List<String> args;
        final String auth;
        final int port;
        final String workDir;
        /** Слепок для сравнения при перезагрузке; содержит пароль — не печатать. */
        final String key;

        ByeDpi(List<String> args, String auth, int port, String workDir, String key) {
            this.args = args;
            this.auth = auth;
            this.port = port;
            this.workDir = workDir;
            this.key = key;
        }
    }

    static final class Root {
        final List<String> start;
        final List<String> stop;
        final String key;

        Root(List<String> start, List<String> stop, String key) {
            this.start = start;
            this.stop = stop;
            this.key = key;
        }
    }

    final String configPath;
    final String workingPath;
    final ByeDpi byedpi;
    final Root root;
    final String notificationTitle;

    private NzRequest(String configPath, String workingPath, ByeDpi byedpi, Root root, String notificationTitle) {
        this.configPath = configPath;
        this.workingPath = workingPath;
        this.byedpi = byedpi;
        this.root = root;
        this.notificationTitle = notificationTitle;
    }

    static File file(Context context) {
        return new File(new File(context.getFilesDir(), "runtime"), "nz-request.json");
    }

    static NzRequest parse(String json) throws JSONException {
        if (json == null || json.trim().isEmpty()) {
            throw new JSONException("пустой запрос");
        }
        JSONObject o = new JSONObject(json);
        String configPath = o.optString("configPath", "");
        if (configPath.isEmpty() || !configPath.startsWith("/")) {
            throw new JSONException("нет configPath (полный путь к конфигу sing-box)");
        }
        String workingPath = text(o, "workingPath");
        String title = text(o, "notificationTitle");

        ByeDpi byedpi = null;
        JSONObject b = o.optJSONObject("byedpi");
        if (b != null) {
            int port = b.optInt("port", 0);
            if (port <= 0 || port > 65535) {
                throw new JSONException("byedpi.port вне 1–65535");
            }
            String auth = text(b, "auth");
            if (auth != null && (auth.indexOf(':') <= 0 || auth.endsWith(":"))) {
                throw new JSONException("byedpi.auth должен быть «имя:пароль»");
            }
            byedpi = new ByeDpi(strings(b.optJSONArray("args")), auth, port, text(b, "workDir"), b.toString());
        }

        Root root = null;
        JSONObject r = o.optJSONObject("root");
        if (r != null) {
            List<String> start = strings(r.optJSONArray("start"));
            if (start.isEmpty()) {
                throw new JSONException("root.start пуст");
            }
            root = new Root(start, strings(r.optJSONArray("stop")), r.toString());
        }
        return new NzRequest(configPath, workingPath, byedpi, root, title);
    }

    /** Записать запрос: сперва во временный файл, потом подменой — чтобы служба не прочла половину. */
    static void save(Context context, String json) throws IOException {
        File target = file(context);
        File dir = target.getParentFile();
        if (dir != null && !dir.isDirectory() && !dir.mkdirs()) {
            throw new IOException("не создать папку " + dir);
        }
        File temp = new File(dir, target.getName() + ".tmp");
        FileOutputStream out = new FileOutputStream(temp);
        try {
            out.write(json.getBytes(UTF8));
            out.getFD().sync();
        } finally {
            out.close();
        }
        if (!temp.renameTo(target)) {
            // renameTo поверх существующего на Android работает; на случай чужой ФС — удалить и повторить.
            target.delete();
            if (!temp.renameTo(target)) {
                throw new IOException("не записать " + target);
            }
        }
    }

    static NzRequest load(Context context) throws IOException, JSONException {
        File f = file(context);
        if (!f.isFile()) {
            throw new IOException("нет сохранённого запроса — запустите из приложения");
        }
        return parse(readText(f));
    }

    static boolean saved(Context context) {
        return file(context).isFile();
    }

    static String readText(File f) throws IOException {
        InputStream in = new FileInputStream(f);
        try {
            ByteArrayOutputStream buffer = new ByteArrayOutputStream((int) Math.max(1024, Math.min(f.length(), 64L << 20)));
            byte[] chunk = new byte[64 * 1024];
            int n;
            while ((n = in.read(chunk)) > 0) {
                buffer.write(chunk, 0, n);
            }
            return new String(buffer.toByteArray(), UTF8);
        } finally {
            in.close();
        }
    }

    /** SHA-256 текста — узнать, сменился ли конфиг, не держа мегабайт в памяти. */
    static String digest(String text) {
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(text.getBytes(UTF8));
            StringBuilder sb = new StringBuilder(hash.length * 2);
            for (byte b : hash) {
                sb.append(Character.forDigit((b >> 4) & 0xF, 16)).append(Character.forDigit(b & 0xF, 16));
            }
            return sb.toString();
        } catch (Exception e) {
            // SHA-256 есть на любом Android; без него — «всегда другой», то есть перезагрузка.
            return Long.toString(System.nanoTime());
        }
    }

    /**
     * Текст ошибки разбора без самого запроса. JSONTokener пишет «… at
     * character N of <весь вход>», а во входе — пароль ByeDPI: хвост после
     * « of » отрезается.
     */
    static String safeMessage(Exception error) {
        String m = error.getMessage();
        if (m == null) {
            return error.getClass().getSimpleName();
        }
        int cut = m.indexOf(" of ");
        if (error instanceof JSONException && cut > 0) {
            m = m.substring(0, cut);
        }
        if (m.length() > 200) {
            m = m.substring(0, 200) + "…";
        }
        return NzSecrets.scrub(m);
    }

    private static String text(JSONObject o, String name) {
        if (o.isNull(name)) {
            return null;
        }
        String v = o.optString(name, "");
        return v.isEmpty() ? null : v;
    }

    private static List<String> strings(JSONArray array) throws JSONException {
        if (array == null) {
            return Collections.emptyList();
        }
        ArrayList<String> list = new ArrayList<>(array.length());
        for (int i = 0; i < array.length(); i++) {
            list.add(array.getString(i));
        }
        return Collections.unmodifiableList(list);
    }
}
