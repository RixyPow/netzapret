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
 * запрос отсюда.
 *
 * Сохраняется запрос не как пришёл, а со снимком того, на что он ссылается
 * ({@link #snapshot}): конфиг и списки групп ByeDPI копируются в папку сеанса
 * {@code runtime/nz-session/<имя>}. Хозяин переписывает singbox.json и
 * runtime/byedpi ещё до NzBox.start, и попытка, оборвавшаяся после записи
 * (отказ checkConfig, ошибка root, сборка без серверов), оставляла бы старый
 * запрос рядом с новыми файлами — плитка и постоянный VPN подняли бы их
 * вперемешку (разбор 27.10).
 *
 * Порт и пароль ByeDPI из запроса служба не берёт: на каждый подъём — свои
 * ({@link ByeDpi#withSession}). В файле они остаются от хозяина, но больше
 * нигде не действуют.
 */
final class NzRequest {
    static final Charset UTF8 = Charset.forName("UTF-8");

    /** Снимки сеансов создаются и чистятся под этим замком — чистка не снесёт ещё не сохранённый. */
    private static final Object SESSIONS = new Object();

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

        /**
         * Тот же ByeDPI на другом порту и с другим паролем. Сохранённый
         * запрос живёт между сеансами VPN, и его порт знает любой, кто
         * просканировал 127.0.0.1, пока ByeDPI работал: занял его, пока VPN
         * выключен, — и при подъёме с плитки ByeDPI не встал бы, а sing-box
         * отдал бы чужому слушателю пароль и все соединения «десинка».
         * Ключ сравнения остаётся от запроса: «тот же ли запрос» решает он.
         */
        ByeDpi withSession(int newPort, String password) {
            String newAuth = null;
            if (auth != null) {
                int colon = auth.indexOf(':');
                newAuth = auth.substring(0, colon + 1) + password;
            }
            return new ByeDpi(withPort(args, newPort), newAuth, newPort, workDir, key);
        }

        /** Имя SOCKS5 (не секрет); null — без пароля. */
        String user() {
            return auth == null ? null : auth.substring(0, auth.indexOf(':'));
        }

        /** Пароль SOCKS5; null — без пароля. */
        String password() {
            return auth == null ? null : auth.substring(auth.indexOf(':') + 1);
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
    /** Имя папки снимка в runtime/nz-session; null — запрос без снимка. */
    final String session;

    private NzRequest(String configPath, String workingPath, ByeDpi byedpi, Root root, String notificationTitle,
            String session) {
        this.configPath = configPath;
        this.workingPath = workingPath;
        this.byedpi = byedpi;
        this.root = root;
        this.notificationTitle = notificationTitle;
        this.session = session;
    }

    static File file(Context context) {
        return new File(new File(context.getFilesDir(), "runtime"), "nz-request.json");
    }

    private static File sessions(Context context) {
        return new File(new File(context.getFilesDir(), "runtime"), "nz-session");
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
        return new NzRequest(configPath, workingPath, byedpi, root, title, text(o, "session"));
    }

    /**
     * Снять снимок запроса и сохранить его. Конфиг — в {@code nz-session/<имя>/config.json},
     * папка ByeDPI ({@code byedpi.workDir}) — в {@code nz-session/<имя>/byedpi}; в
     * сохранённом запросе пути заменены на снимок, в аргументах ByeDPI — тоже
     * (списки групп ByeDpiCommandLine передаёт полными путями от workDir).
     * Служба читает только снимок, и всё, что хозяин перепишет до следующего
     * успешного start, его не трогает. Запрос уже разобран {@link #parse}.
     */
    static void snapshotAndSave(Context context, String json) throws IOException, JSONException {
        synchronized (SESSIONS) {
            JSONObject o = new JSONObject(json);
            File root = sessions(context);
            if (!root.isDirectory() && !root.mkdirs()) {
                throw new IOException("не создать папку " + root);
            }
            File dir = null;
            for (int attempt = 0; attempt < 10 && dir == null; attempt++) {
                File candidate = new File(root, Long.toString(System.currentTimeMillis(), 36) + "-" + attempt);
                if (candidate.mkdir()) {
                    dir = candidate;
                }
            }
            if (dir == null) {
                throw new IOException("не создать папку снимка в " + root);
            }
            boolean saved = false;
            try {
                File config = new File(dir, "config.json");
                copyFile(new File(o.getString("configPath")), config);
                o.put("configPath", config.getAbsolutePath());
                o.put("session", dir.getName());

                JSONObject b = o.optJSONObject("byedpi");
                String workDir = b != null ? text(b, "workDir") : null;
                if (workDir != null) {
                    File from = new File(workDir);
                    File to = new File(dir, "byedpi");
                    if (from.isDirectory()) {
                        copyTree(from, to);
                    } else if (!to.mkdirs()) {
                        throw new IOException("не создать папку " + to);
                    }
                    // Строкой как пришла: хозяин склеивает пути списков из неё же (InWork).
                    String oldPrefix = trimSlash(workDir) + "/";
                    String newPrefix = to.getAbsolutePath() + "/";
                    JSONArray args = b.optJSONArray("args");
                    if (args != null) {
                        JSONArray moved = new JSONArray();
                        for (int i = 0; i < args.length(); i++) {
                            moved.put(args.getString(i).replace(oldPrefix, newPrefix));
                        }
                        b.put("args", moved);
                    }
                    b.put("workDir", to.getAbsolutePath());
                }
                save(context, o.toString());
                saved = true;
            } finally {
                if (!saved) {
                    deleteTree(dir);
                }
            }
        }
    }

    /**
     * Стереть снимки, на которые больше ничто не ссылается: оставить снимок
     * сохранённого запроса и снимок работающего движка ({@code live}). Не
     * прочитался сохранённый запрос — не трогать ничего: стереть его снимок
     * из-за сбоя чтения значило бы сломать плитку.
     */
    static void prune(Context context, String live) {
        synchronized (SESSIONS) {
            String saved;
            try {
                saved = saved(context) ? load(context).session : null;
            } catch (Exception e) {
                return;
            }
            File[] dirs = sessions(context).listFiles();
            if (dirs == null) {
                return;
            }
            for (File d : dirs) {
                String name = d.getName();
                if (!name.equals(saved) && !name.equals(live)) {
                    deleteTree(d);
                }
            }
        }
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

    /**
     * Конфиг, в котором исходящий SOCKS на ByeDPI ({@code type: socks},
     * сервер на петле, порт запроса {@code fromPort}) смотрит на порт и
     * пароль этой сессии. Остальное не трогается; разбор и запись — org.json
     * (порядок ключей тот же, «/» пишется как «\/» — для JSON это то же).
     * Такого исходящего нет — конфиг как был.
     */
    static String pointDesync(String config, int fromPort, ByeDpi session) throws JSONException {
        // Дёшево отсеять конфиг без SOCKS: разбирать мегабайт «Туннеля» зря незачем.
        if (!config.contains("\"socks\"")) {
            return config;
        }
        JSONObject root = new JSONObject(config);
        JSONArray outbounds = root.optJSONArray("outbounds");
        if (outbounds == null) {
            return config;
        }
        int changed = 0;
        for (int i = 0; i < outbounds.length(); i++) {
            JSONObject outbound = outbounds.optJSONObject(i);
            if (outbound == null || !"socks".equals(outbound.optString("type"))
                    || !loopback(outbound.optString("server")) || outbound.optInt("server_port", -1) != fromPort) {
                continue;
            }
            outbound.put("server_port", session.port);
            if (session.auth != null) {
                outbound.put("username", session.user());
                outbound.put("password", session.password());
            }
            changed++;
        }
        if (changed == 0) {
            NzBox.LOG.add(NzLog.NZ, "в конфиге нет исходящего SOCKS на ByeDPI (порт запроса " + fromPort
                    + ") — «десинк» через ByeDPI не пойдёт");
            return config;
        }
        return root.toString();
    }

    /** Значение -p/--port заменено на {@code port}; ключа нет — дописан в начало (как WithListener у хозяина). */
    static List<String> withPort(List<String> args, int port) {
        String value = Integer.toString(port);
        ArrayList<String> out = new ArrayList<>(args.size() + 2);
        boolean found = false;
        for (int i = 0; i < args.size(); i++) {
            String a = args.get(i);
            if (("-p".equals(a) || "--port".equals(a)) && i + 1 < args.size()) {
                out.add(a);
                out.add(value);
                i++;
                found = true;
            } else if (a.startsWith("--port=")) {
                out.add("--port=" + value);
                found = true;
            } else if (a.length() > 2 && a.startsWith("-p") && digits(a.substring(2))) {
                out.add("-p" + value);
                found = true;
            } else {
                out.add(a);
            }
        }
        if (!found) {
            out.add(0, "-p");
            out.add(1, value);
        }
        return Collections.unmodifiableList(out);
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

    private static boolean loopback(String host) {
        return "127.0.0.1".equals(host) || "localhost".equals(host) || "::1".equals(host);
    }

    private static boolean digits(String s) {
        if (s.isEmpty()) {
            return false;
        }
        for (int i = 0; i < s.length(); i++) {
            if (s.charAt(i) < '0' || s.charAt(i) > '9') {
                return false;
            }
        }
        return true;
    }

    private static String trimSlash(String path) {
        String p = path;
        while (p.length() > 1 && p.endsWith("/")) {
            p = p.substring(0, p.length() - 1);
        }
        return p;
    }

    private static void copyFile(File from, File to) throws IOException {
        InputStream in = new FileInputStream(from);
        try {
            FileOutputStream out = new FileOutputStream(to);
            try {
                byte[] chunk = new byte[64 * 1024];
                int n;
                while ((n = in.read(chunk)) > 0) {
                    out.write(chunk, 0, n);
                }
                // Запрос, сохранённый следом, ссылается на этот файл — он должен лечь на диск раньше.
                out.getFD().sync();
            } finally {
                out.close();
            }
        } finally {
            in.close();
        }
    }

    private static void copyTree(File from, File to) throws IOException {
        if (!to.isDirectory() && !to.mkdirs()) {
            throw new IOException("не создать папку " + to);
        }
        File[] children = from.listFiles();
        if (children == null) {
            throw new IOException("не прочитать папку " + from);
        }
        for (File child : children) {
            File target = new File(to, child.getName());
            if (child.isDirectory()) {
                copyTree(child, target);
            } else {
                copyFile(child, target);
            }
        }
    }

    private static void deleteTree(File f) {
        File[] children = f.listFiles();
        if (children != null) {
            for (File child : children) {
                deleteTree(child);
            }
        }
        f.delete();
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
