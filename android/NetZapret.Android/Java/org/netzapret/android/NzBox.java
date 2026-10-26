package org.netzapret.android;

import android.content.Context;
import android.content.Intent;
import android.net.VpnService;
import android.os.Build;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;

import io.nekohasekai.libbox.Libbox;

/**
 * Мост C# ↔ Java (DESIGN.md §4): только статические методы с простыми
 * типами, JSON строками. В C# нет ни одного типа libbox — привязка gomobile
 * роняет мост сборщика мусора Mono (образец VPNRouter), поэтому всё, что
 * трогает libbox, здесь, а C# зовёт эти шесть методов сырым JNI (NzBoxBridge).
 *
 * Ни один публичный метод не бросает: исключение Java в C# пришло бы
 * через JNI посреди потока окна. Ошибка — состоянием «failed» с текстом,
 * в ответе команды — {"error":"…"}, у checkConfig — непустой строкой.
 *
 * Запуск и остановка идут через одну очередь {@link #GATE} — как EngineGate
 * у Windows: два запуска подряд иначе подняли бы два движка.
 */
public final class NzBox {
    static final String STOPPED = "stopped";
    static final String STARTING = "starting";
    static final String RUNNING = "running";
    static final String STOPPING = "stopping";
    static final String FAILED = "failed";

    /** Рассылается своему пакету при смене состояния — чтобы C# не опрашивал status() зря. */
    public static final String ACTION_STATUS_CHANGED = "org.netzapret.android.STATUS_CHANGED";

    static final NzLog LOG = new NzLog(1000);

    static final ExecutorService GATE = Executors.newSingleThreadExecutor(new ThreadFactory() {
        @Override
        public Thread newThread(Runnable r) {
            Thread t = new Thread(r, "nz-engine-gate");
            t.setDaemon(true);
            return t;
        }
    });

    private static final Object LOCK = new Object();
    private static String state = STOPPED;
    private static String error;
    private static long startedAt;

    /** Хочет ли человек (или система — постоянный VPN), чтобы движок работал. */
    private static volatile boolean desired;
    private static volatile Context appContext;
    private static volatile String title = "NetZapret";

    private NzBox() {
    }

    // ================= публичный договор =================

    /**
     * Записать запрос в files/runtime/nz-request.json и поднять службу VPN
     * на переднем плане. Если движок уже работает — служба перечитает запрос
     * и перезагрузит конфиг (ByeDPI и root — только если их запрос сменился).
     * Разрешение на VPN (VpnService.prepare) должно быть выдано заранее,
     * из активности: из службы окно разрешения не показать.
     */
    public static void start(Context context, String requestJson) {
        try {
            remember(context);
            NzRequest request;
            try {
                request = NzRequest.parse(requestJson);
            } catch (Exception e) {
                fail("запрос на запуск не разобран: " + NzRequest.safeMessage(e));
                return;
            }
            if (request.byedpi != null && request.byedpi.auth != null) {
                NzSecrets.register(request.byedpi.auth);
            }
            try {
                NzRequest.save(context, requestJson);
            } catch (Exception e) {
                fail("запрос на запуск не записан: " + NzSecrets.describe(e));
                return;
            }
            launch(context);
        } catch (Throwable t) {
            fail("запуск не удался: " + NzSecrets.describe(t));
        }
    }

    /** Остановить движки и VPN. Без работающей службы — просто «stopped». */
    public static void stop(Context context) {
        try {
            remember(context);
            desired = false;
            NzVpnService service = NzVpnService.instance;
            if (service != null) {
                service.requestStop(null);
                return;
            }
            synchronized (LOCK) {
                state = STOPPED;
                error = null;
                startedAt = 0;
            }
            changed();
        } catch (Throwable t) {
            LOG.add(NzLog.NZ, "остановка: " + NzSecrets.describe(t));
        }
    }

    /** Состояние JSON-ом (DESIGN.md §4); лишние поля C# пропускает. */
    public static String status() {
        try {
            JSONObject o = new JSONObject();
            synchronized (LOCK) {
                o.put("state", state);
                o.put("error", error == null ? JSONObject.NULL : error);
                o.put("startedAt", startedAt > 0 ? (Object) startedAt : JSONObject.NULL);
            }
            NzVpnService service = NzVpnService.instance;
            NzByeDpi byedpi = service != null ? service.byedpi() : null;
            NzRoot root = service != null ? service.root() : null;
            o.put("byedpi", byedpi != null ? byedpi.status() : JSONObject.NULL);
            o.put("root", root != null ? root.status() : JSONObject.NULL);
            o.put("log", new JSONArray(LOG.tail(100, NzLog.ALL)));
            return o.toString();
        } catch (Throwable t) {
            return "{\"state\":\"failed\",\"error\":" + JSONObject.quote("status(): " + NzSecrets.describe(t)) + "}";
        }
    }

    /**
     * Команды движку (DESIGN.md §4). Блокирует до ~3 с (разовый клиент
     * сервера команд) — звать не из потока окна.
     *   groups       → [{tag,type,selected,selectable,items:[{tag,type,delay,time}]}]
     *   select       {group,outbound}           → {"ok":true}
     *   urltest      {group|outbound}           → {"ok":true}; группе больше чем из одного — отказ
     *   connections  {state:active|closed|all, limit} → [{id,…}]
     *   closeConnection {id}, closeConnections  → {"ok":true}
     *   clashMode    {mode} — сменить; без mode → {mode,modes}
     *   logs         {source:all|engine|nz|byedpi|root, limit} → ["строка", …]
     */
    public static String command(String name, String argJson) {
        try {
            JSONObject arg;
            try {
                String trimmed = argJson == null ? "" : argJson.trim();
                arg = trimmed.isEmpty() || "null".equals(trimmed) ? new JSONObject() : new JSONObject(trimmed);
            } catch (Exception e) {
                return errorJson("аргумент не JSON: " + NzRequest.safeMessage(e));
            }
            if (name == null) {
                return errorJson("нет имени команды");
            }
            if ("logs".equals(name)) {
                return logs(arg);
            }
            NzVpnService service = NzVpnService.instance;
            NzCommands commands = service != null ? service.commands() : null;
            if (commands == null) {
                return errorJson("движок не запущен");
            }
            switch (name) {
                case "groups":
                    return commands.groups(2000);
                case "select": {
                    String group = arg.optString("group", "");
                    String outbound = arg.optString("outbound", "");
                    if (group.isEmpty() || outbound.isEmpty()) {
                        return errorJson("select: нужны group и outbound");
                    }
                    NzCommands.select(group, outbound);
                    return okJson();
                }
                case "urltest": {
                    String tag = arg.optString("group", "");
                    if (tag.isEmpty()) {
                        tag = arg.optString("outbound", arg.optString("tag", ""));
                    }
                    if (tag.isEmpty()) {
                        return errorJson("urltest: нужен group или outbound");
                    }
                    // Группа меряет всех своих разом, а продавцы закрывают вход за залп
                    // (CLAUDE.md, «Продавцов не бить залпом»): только сервер или группа из одного.
                    int size = commands.groupSize(tag);
                    if (size > 1) {
                        return errorJson("urltest: «" + tag + "» — группа из " + size
                                + " серверов, она мерила бы всех разом; передайте один сервер");
                    }
                    NzCommands.urlTest(tag);
                    return okJson();
                }
                case "connections":
                    return NzCommands.connections(arg.optString("state", "active"),
                            Math.max(1, arg.optInt("limit", 500))).toString();
                case "closeConnection": {
                    String id = arg.optString("id", "");
                    if (id.isEmpty()) {
                        return errorJson("closeConnection: нужен id");
                    }
                    NzCommands.closeConnection(id);
                    return okJson();
                }
                case "closeConnections":
                    NzCommands.closeConnections();
                    return okJson();
                case "clashMode": {
                    String mode = arg.optString("mode", "");
                    if (mode.isEmpty()) {
                        return commands.clashModeStatus().toString();
                    }
                    NzCommands.setClashMode(mode);
                    return okJson();
                }
                default:
                    return errorJson("неизвестная команда: " + name);
            }
        } catch (Throwable t) {
            return errorJson(NzSecrets.describe(t));
        }
    }

    /** Проверка конфига libbox: "" — годен, иначе текст ошибки без секретов. */
    public static String checkConfig(String configJson) {
        try {
            Context context = context();
            if (context != null) {
                NzLibbox.ensure(context, null);
            }
            Libbox.checkConfig(configJson == null ? "" : configJson);
            return "";
        } catch (Throwable t) {
            String message = NzSecrets.describe(t);
            return message.isEmpty() ? "конфиг не прошёл проверку" : message;
        }
    }

    /** Версия libbox (1.14.3-extended-2.7.3); пусто — библиотека не загрузилась. */
    public static String version() {
        try {
            return Libbox.version();
        } catch (Throwable t) {
            return "";
        }
    }

    // ================= для своих классов =================

    /** Плитка и прочие запуски без C#: последний сохранённый запрос. false — запроса нет. */
    static boolean startSaved(Context context) {
        try {
            remember(context);
            if (!NzRequest.saved(context)) {
                fail("нет сохранённого запроса — запустите из приложения");
                return false;
            }
            launch(context);
            return true;
        } catch (Throwable t) {
            fail("запуск не удался: " + NzSecrets.describe(t));
            return false;
        }
    }

    private static void launch(Context context) {
        Intent prepare;
        try {
            prepare = VpnService.prepare(context);
        } catch (Throwable t) {
            prepare = null;
        }
        if (prepare != null) {
            fail("нет разрешения на VPN — откройте NetZapret и разрешите подключение");
            return;
        }
        desired = true;
        synchronized (LOCK) {
            if (!RUNNING.equals(state)) {
                state = STARTING;
                error = null;
            }
        }
        Intent intent = new Intent(context, NzVpnService.class).setAction(NzVpnService.ACTION_START);
        try {
            if (Build.VERSION.SDK_INT >= 26) {
                context.startForegroundService(intent);
            } else {
                context.startService(intent);
            }
        } catch (Throwable t) {
            // Работающий движок при этом жив — его «хочу» не трогаем.
            if (!RUNNING.equals(state())) {
                desired = false;
            }
            fail("Android не дал запустить службу VPN: " + NzSecrets.describe(t));
            return;
        }
        changed();
    }

    static void remember(Context context) {
        if (context != null && appContext == null) {
            Context app = context.getApplicationContext();
            appContext = app != null ? app : context;
        }
    }

    /**
     * Контекст приложения для checkConfig, которому его не передают.
     * Если ни start, ни служба ещё не звались — ActivityThread.currentApplication
     * (скрытый, но разрешённый для приложений метод); не вышло — проверка без
     * Libbox.setup, относительные пути конфига тогда считаются от «/».
     */
    static Context context() {
        Context c = appContext;
        if (c != null) {
            return c;
        }
        try {
            Object app = Class.forName("android.app.ActivityThread").getMethod("currentApplication").invoke(null);
            if (app instanceof Context) {
                remember((Context) app);
            }
        } catch (Throwable ignored) {
        }
        return appContext;
    }

    static boolean desired() {
        return desired;
    }

    static void setDesired(boolean value) {
        desired = value;
    }

    static String sessionTitle() {
        return title;
    }

    static void setTitle(String value) {
        title = value == null || value.isEmpty() ? "NetZapret" : value;
    }

    static String state() {
        synchronized (LOCK) {
            return state;
        }
    }

    static String error() {
        synchronized (LOCK) {
            return error;
        }
    }

    static void setState(String newState, String newError) {
        synchronized (LOCK) {
            state = newState;
            error = newError == null ? null : NzSecrets.scrub(newError);
            if (!RUNNING.equals(newState) && !STARTING.equals(newState)) {
                startedAt = 0;
            }
        }
        changed();
    }

    static void setRunning() {
        synchronized (LOCK) {
            state = RUNNING;
            error = null;
            if (startedAt == 0) {
                startedAt = System.currentTimeMillis();
            }
        }
        changed();
    }

    /**
     * Отказ запуска. Если движок уже работает (отказала перезагрузка ещё до
     * службы — кривой запрос, нет записи), он работает по-прежнему: «failed»
     * соврал бы, причина остаётся в журнале и в error.
     */
    static void fail(String message) {
        LOG.add(NzLog.NZ, message);
        synchronized (LOCK) {
            if (RUNNING.equals(state)) {
                error = NzSecrets.scrub(message);
            } else {
                state = FAILED;
                error = NzSecrets.scrub(message);
                startedAt = 0;
            }
        }
        changed();
    }

    /** Текст строки уведомления по состоянию. */
    static String notificationText() {
        String s;
        String e;
        synchronized (LOCK) {
            s = state;
            e = error;
        }
        if (RUNNING.equals(s)) {
            return "Работает";
        }
        if (STARTING.equals(s)) {
            return "Запускается…";
        }
        if (STOPPING.equals(s)) {
            return "Останавливается…";
        }
        if (FAILED.equals(s)) {
            return e != null ? "Ошибка: " + e : "Ошибка";
        }
        return "Остановлен";
    }

    /** Состояние сменилось: плитка, уведомление службы и рассылка своему пакету. */
    static void changed() {
        Context c = appContext;
        try {
            NzVpnService service = NzVpnService.instance;
            if (service != null) {
                service.refreshNotification();
            }
        } catch (Throwable ignored) {
        }
        if (c == null) {
            return;
        }
        NzTileService.requestUpdate(c);
        try {
            c.sendBroadcast(new Intent(ACTION_STATUS_CHANGED).setPackage(c.getPackageName()));
        } catch (Throwable ignored) {
        }
    }

    private static String logs(JSONObject arg) {
        String source = arg.optString("source", "all");
        int limit = Math.max(1, arg.optInt("limit", 1000));
        int which;
        switch (source) {
            case "engine":
                which = NzLog.ENGINE;
                break;
            case "nz":
                which = NzLog.NZ;
                break;
            case "byedpi": {
                // Свои 200 строк ByeDPI, пока он поднят: в общем кольце их вытесняет движок.
                NzVpnService service = NzVpnService.instance;
                NzByeDpi byedpi = service != null ? service.byedpi() : null;
                if (byedpi != null) {
                    List<String> own = byedpi.lines();
                    return new JSONArray(own.subList(Math.max(0, own.size() - limit), own.size())).toString();
                }
                which = NzLog.BYEDPI;
                break;
            }
            case "root":
                which = NzLog.ROOT;
                break;
            default:
                which = NzLog.ALL;
                break;
        }
        List<String> lines = LOG.tail(limit, which);
        return new JSONArray(lines).toString();
    }

    private static String okJson() {
        return "{\"ok\":true}";
    }

    private static String errorJson(String message) {
        return "{\"error\":" + JSONObject.quote(NzSecrets.scrub(message)) + "}";
    }
}
