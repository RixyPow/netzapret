package org.netzapret.android;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ServiceInfo;
import android.graphics.drawable.Icon;
import android.net.VpnService;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.os.SystemClock;

import java.io.File;
import java.io.IOException;

import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.CommandServerHandler;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.OverrideOptions;
import io.nekohasekai.libbox.SystemProxyStatus;

/**
 * Служба VPN — на телефоне то же, что надзор на Windows: поднимает движки
 * по порядку, держит их и гасит в обратном (libbox.md §4.2, DESIGN.md).
 *
 * Порядок запуска: прежний libbox докрылся → команда root (nfqws2) → ByeDPI
 * на своих порту и пароле и ожидание его готовности (до establish: ребёнок не
 * унаследует TUN) → Libbox.setup → сервер команд → startOrReloadService с
 * конфигом, исходящий SOCKS которого переписан на этот порт и пароль.
 * Остановка — наоборот. Всё это идёт в очереди
 * {@link NzBox#GATE}; поля движков трогает только она, status() читает
 * volatile-ссылки.
 *
 * Запускается тремя путями: NzBox.start (наш ACTION_START), плитка (тоже
 * через NzBox) и постоянный VPN системы (действие android.net.VpnService
 * без нашего запроса) — тогда служба берёт сохранённый запрос из файла.
 */
public final class NzVpnService extends VpnService {
    static final String ACTION_START = "org.netzapret.android.START";
    static final String ACTION_STOP = "org.netzapret.android.STOP";

    private static final int NOTIFICATION_ID = 0x4e5a;
    private static final String CHANNEL_ID = "nz-engine";
    // Сколько ждать закрытия libbox: зависшая остановка не должна держать очередь вечно (как у VPNRouter).
    private static final long CLOSE_WAIT_MS = 4000;
    // Сколько новый запуск ждёт, пока докроется прежний libbox (всё равно в очереди, а не в Go).
    private static final long PREVIOUS_CLOSE_WAIT_MS = 30_000;
    private static final long BYEDPI_READY_MS = 5000;
    // Переездов ByeDPI с занятого порта за один запуск; больше — это не случайность, гасим.
    private static final int MAX_BYEDPI_MOVES = 3;
    private static final String STILL_CLOSING = "движок ещё закрывается";

    static volatile NzVpnService instance;

    /**
     * Поток закрытия последнего libbox. Статический: служба могла смениться,
     * а Go закрывает экземпляр дальше и после того, как очередь перестала
     * его ждать (CLOSE_WAIT_MS). Пока он жив, новый сервер команд не
     * поднимается, а «остановлен» не пишется: прежний движок ещё держит
     * command.sock, cache.db и свою копию дескриптора TUN — VPN жив.
     */
    private static volatile Thread closer;
    // Только из очереди: вместо «stopped» выставлено «ещё закрывается» — снять по окончании, с этой причиной.
    private static boolean closeNotice;
    private static String closeReason;

    private volatile NzByeDpi byedpi;
    private volatile NzRoot root;
    private volatile NzCommands commands;
    private volatile boolean foreground;
    /**
     * Номер последнего onStartCommand. Задача очереди гасит службу, только
     * если после неё не пришло нового запуска: иначе stopSelf и снятие
     * переднего плана убили бы запуск, который уже стоит в очереди.
     */
    private volatile int lastStartId;
    private final Handler main = new Handler(Looper.getMainLooper());

    // Ниже — только из очереди NzBox.GATE.
    private NzRequest request;
    private String configDigest;
    /** ByeDPI этого запуска: свои порт и пароль, не из запроса (NzRequest.ByeDpi.withSession). */
    private NzRequest.ByeDpi dpi;
    private int dpiMoves;
    private NzNetworkMonitor monitor;
    private NzPlatform platform;
    private volatile CommandServer server;
    private BroadcastReceiver idleReceiver;
    private boolean engineUp;

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        NzBox.remember(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, final int startId) {
        lastStartId = startId;
        String action = intent != null ? intent.getAction() : null;
        if (ACTION_STOP.equals(action)) {
            // Кнопка «Остановить» в уведомлении: обычный startService, startForeground не нужен.
            NzBox.setDesired(false);
            requestStop(null);
            return START_NOT_STICKY;
        }
        // Всё прочее пришло через startForegroundService (наш запуск, плитка, постоянный VPN):
        // startForeground обязателен за секунды, даже если тут же остановимся.
        if (!enterForeground()) {
            NzBox.setDesired(false);
            NzBox.fail("служба VPN не смогла выйти на передний план");
            stopSelf(startId);
            return START_NOT_STICKY;
        }
        if (!ACTION_START.equals(action)) {
            // Нас подняла система (постоянный VPN) — никто, кроме неё, этого не просил, значит, работать.
            NzBox.LOG.add(NzLog.NZ, "служба поднята системой (" + (action == null ? "без действия" : action) + ")");
            NzBox.setDesired(true);
        }
        NzBox.GATE.execute(new Runnable() {
            @Override
            public void run() {
                startEngine(startId);
            }
        });
        // Не «липкая»: после гибели процесса её поднимет постоянный VPN, если он включён, а не сама Android.
        return START_NOT_STICKY;
    }

    @Override
    public void onRevoke() {
        // Не super.onRevoke(): тот сразу зовёт stopSelf, а движки надо сначала погасить.
        NzBox.setDesired(false);
        requestStop("VPN выключен системой: включён другой VPN или снято разрешение");
    }

    @Override
    public void onDestroy() {
        if (instance == this) {
            instance = null;
        }
        NzBox.GATE.execute(new Runnable() {
            @Override
            public void run() {
                if (engineUp || server != null || byedpi != null || root != null) {
                    NzBox.LOG.add(NzLog.NZ, "служба уничтожена системой — гашу движки");
                    teardown();
                    markStopped(NzBox.error());
                }
            }
        });
        super.onDestroy();
    }

    /** Остановить из любого потока: в очередь, после текущего запуска. */
    void requestStop(final String reason) {
        final int startId = lastStartId;
        NzBox.GATE.execute(new Runnable() {
            @Override
            public void run() {
                stopEngine(reason, startId);
            }
        });
    }

    NzByeDpi byedpi() {
        return byedpi;
    }

    NzRoot root() {
        return root;
    }

    NzCommands commands() {
        return commands;
    }

    VpnService.Builder newBuilder() {
        return new Builder();
    }

    // ================= очередь: запуск, перезагрузка, остановка =================

    private void startEngine(int startId) {
        try {
            startOrReload(startId);
        } finally {
            try {
                // Снимки прежних запросов (NzRequest.snapshotAndSave) больше никому не нужны.
                NzRequest.prune(this, request != null ? request.session : null);
            } catch (Throwable ignored) {
            }
        }
    }

    private void startOrReload(int startId) {
        if (!NzBox.desired()) {
            // «Остановить» пришло раньше, чем служба успела начать.
            if (!engineUp) {
                finishStopped(null, startId);
            }
            return;
        }
        NzRequest next;
        try {
            next = NzRequest.load(this);
        } catch (Exception e) {
            String why = "запрос на запуск не прочитан: " + NzRequest.safeMessage(e);
            NzBox.LOG.add(NzLog.NZ, why);
            if (engineUp) {
                // Работающий движок не трогаем: прежний конфиг лучше, чем никакого.
                NzBox.setRunning();
            } else {
                finish(NzBox.FAILED, why, startId);
            }
            return;
        }
        NzBox.setTitle(next.notificationTitle);
        if (engineUp) {
            reload(next, startId);
        } else {
            fullStart(next, startId);
        }
    }

    private void fullStart(NzRequest req, int startId) {
        NzBox.setState(NzBox.STARTING, null);
        closeNotice = false;
        NzBox.LOG.add(NzLog.NZ, "запуск движков");
        try {
            if (!awaitPreviousClose()) {
                if (!NzBox.desired()) {
                    NzBox.LOG.add(NzLog.NZ, "запуск прерван остановкой");
                    finishStopped(null, startId);
                    return;
                }
                // Второй сервер команд встал бы на тот же command.sock, а поздний close()
                // прежнего стёр бы его; cache.db держит замок прежнего экземпляра.
                throw new IllegalStateException("прежний движок не закрылся за "
                        + (PREVIOUS_CLOSE_WAIT_MS / 1000) + " с — новый не поднимаю. "
                        + "Закройте NetZapret из недавних приложений и запустите снова");
            }
            // Хэш — от конфига хозяина: по нему перезагрузка узнаёт «тот же запрос».
            String original = NzRequest.readText(new File(req.configPath));
            String config = original;

            if (req.root != null) {
                NzRoot r = new NzRoot(req.root);
                root = r;
                r.start();
            }
            dpiMoves = 0;
            if (req.byedpi != null) {
                startByeDpi(req.byedpi);
                config = NzRequest.pointDesync(original, req.byedpi.port, dpi);
            }
            NzLibbox.ensure(this, req.workingPath);
            if (!NzBox.desired()) {
                NzBox.LOG.add(NzLog.NZ, "запуск прерван остановкой");
                teardown();
                finishStopped(null, startId);
                return;
            }

            // Сеть по умолчанию — до движка: Go спросит её сразу при старте (libbox.md §3.2).
            NzNetworkMonitor m = new NzNetworkMonitor(this);
            monitor = m;
            m.start();
            platform = new NzPlatform(this, m);

            CommandServer s = Libbox.newCommandServer(new ServerHandler(), platform);
            server = s;
            s.start();
            s.startOrReloadService(config, overrides());

            engineUp = true;
            request = req;
            configDigest = NzRequest.digest(original);
            NzCommands c = new NzCommands();
            c.open();
            commands = c;
            registerIdle();
            NzBox.setRunning();
            NzBox.LOG.add(NzLog.NZ, "движки запущены (libbox " + NzBox.version() + ")");
        } catch (Throwable t) {
            String why = NzSecrets.describe(t);
            NzBox.LOG.add(NzLog.NZ, "запуск не удался: " + why);
            teardown();
            finish(NzBox.FAILED, why, startId);
        }
    }

    /**
     * Новый запрос при работающем движке: конфиг — перезагрузкой libbox
     * (VPN переоткрывается, приложения соединения не теряют надолго),
     * ByeDPI и root — только если их часть запроса сменилась.
     */
    private void reload(NzRequest req, int startId) {
        try {
            String original = NzRequest.readText(new File(req.configPath));
            String digest = NzRequest.digest(original);
            NzRequest old = request;

            String oldRoot = old != null && old.root != null ? old.root.key : null;
            String newRoot = req.root != null ? req.root.key : null;
            String oldDpi = old != null && old.byedpi != null ? old.byedpi.key : null;
            String newDpi = req.byedpi != null ? req.byedpi.key : null;
            if (digest.equals(configDigest) && same(oldRoot, newRoot) && same(oldDpi, newDpi)) {
                // Постоянный VPN системы и повторное «Запустить» шлют тот же запрос:
                // переоткрывать VPN ради него — рвать соединения зря.
                request = req;
                NzBox.setRunning();
                return;
            }
            NzBox.setState(NzBox.STARTING, null);
            NzBox.LOG.add(NzLog.NZ, "перезагрузка конфига");
            if (!same(oldRoot, newRoot)) {
                NzRoot r = root;
                root = null;
                if (r != null) {
                    r.stop();
                }
                if (req.root != null) {
                    NzRoot fresh = new NzRoot(req.root);
                    root = fresh;
                    fresh.start();
                }
            }

            if (!same(oldDpi, newDpi)) {
                NzByeDpi b = byedpi;
                byedpi = null;
                dpi = null;
                if (b != null) {
                    b.stop();
                }
                dpiMoves = 0;
                if (req.byedpi != null) {
                    startByeDpi(req.byedpi);
                }
            }
            // ByeDPI тот же — конфиг ведётся на его нынешние порт и пароль: ключ запроса
            // совпал, значит, и порт запроса, по которому ищется исходящий, тот же.
            String config = req.byedpi != null && dpi != null
                    ? NzRequest.pointDesync(original, req.byedpi.port, dpi) : original;

            server.startOrReloadService(config, overrides());
            request = req;
            configDigest = digest;
            NzBox.setRunning();
            NzBox.LOG.add(NzLog.NZ, "конфиг перезагружен");
        } catch (Throwable t) {
            // libbox закрывает прежний экземпляр до запуска нового: после отказа движка нет — гасим всё.
            String why = NzSecrets.describe(t);
            NzBox.LOG.add(NzLog.NZ, "перезагрузка не удалась: " + why);
            teardown();
            finish(NzBox.FAILED, why, startId);
        }
    }

    /**
     * ByeDPI на новых порту и пароле — на каждый подъём, не из сохранённого
     * запроса (NzRequest.ByeDpi.withSession): тот же порт после выключения
     * VPN мог занять чужой слушатель. Свободного порта нет — исключение:
     * указать sing-box безопасный порт тогда нечем.
     */
    private void startByeDpi(NzRequest.ByeDpi requested) throws IOException {
        int port = NzByeDpi.freePort();
        if (port == 0) {
            throw new IOException("на 127.0.0.1 не нашлось свободного порта для ByeDPI (50 попыток)");
        }
        NzRequest.ByeDpi session = requested.withSession(port, NzByeDpi.newPassword());
        NzByeDpi b = new NzByeDpi(this, session, portLoss);
        byedpi = b;
        dpi = session;
        b.start();
        if (b.waitReady(BYEDPI_READY_MS)) {
            NzBox.LOG.add(NzLog.NZ, "ByeDPI готов на 127.0.0.1:" + session.port);
        } else {
            // Не роняем VPN: туннель и «напрямую» работают и без десинка, а надзор
            // ByeDPI продолжит перезапуски — как winws2 на Windows («движок не работает»).
            NzBox.LOG.add(NzLog.NZ, "ByeDPI не ответил за " + (BYEDPI_READY_MS / 1000)
                    + " с — VPN поднимается без десинка, надзор перезапустит ByeDPI");
        }
    }

    /** Надзор ByeDPI нашёл его порт за чужим слушателем — переезд в очереди. */
    private final NzByeDpi.PortLoss portLoss = new NzByeDpi.PortLoss() {
        @Override
        public void portLost(final NzByeDpi which) {
            NzBox.GATE.execute(new Runnable() {
                @Override
                public void run() {
                    moveByeDpi(which);
                }
            });
        }
    };

    /**
     * ByeDPI на другой порт и перезагрузка конфига: до неё sing-box ведёт
     * «десинк» (и всё без правила — DesyncSocksDefault) на порт, который слушает
     * чужая программа, вместе с паролем. VPN при перезагрузке переоткрывается.
     */
    private void moveByeDpi(NzByeDpi from) {
        NzRequest req = request;
        CommandServer s = server;
        if (!engineUp || byedpi != from || req == null || req.byedpi == null || s == null) {
            // Устарело: движок с тех пор остановлен или ByeDPI уже сменён.
            return;
        }
        dpiMoves++;
        try {
            byedpi = null;
            dpi = null;
            from.stop();
            if (dpiMoves > MAX_BYEDPI_MOVES) {
                // Каждый раз новый случайный порт — значит, его занимают нарочно или ByeDPI
                // уходит в фон сам (-D в своей строке). Держать VPN, ведущий «десинк» неведомо куда, нельзя.
                throw new IOException("порт ByeDPI в " + dpiMoves
                        + "-й раз за запуск занят другой программой — движки остановлены");
            }
            String original = NzRequest.readText(new File(req.configPath));
            startByeDpi(req.byedpi);
            s.startOrReloadService(NzRequest.pointDesync(original, req.byedpi.port, dpi), overrides());
            NzBox.LOG.add(NzLog.NZ, "ByeDPI переехал на 127.0.0.1:" + dpi.port + ", конфиг перезагружен");
            NzBox.setRunning();
        } catch (Throwable t) {
            String why = NzSecrets.describe(t);
            NzBox.LOG.add(NzLog.NZ, "переезд ByeDPI не удался: " + why);
            teardown();
            finish(NzBox.FAILED, why, lastStartId);
        }
    }

    /** OverrideOptions — никогда не null (Go разыменует его, command_server.go:216). */
    private static OverrideOptions overrides() {
        OverrideOptions overrides = new OverrideOptions();
        // false: true включил бы auto_redirect, которому нужен root и iptables nfqws2.
        overrides.setAutoRedirect(false);
        return overrides;
    }

    private void stopEngine(String reason, int startId) {
        boolean had = engineUp || server != null || byedpi != null || root != null;
        if (had) {
            NzBox.setState(NzBox.STOPPING, null);
            NzBox.LOG.add(NzLog.NZ, reason != null ? "остановка: " + reason : "остановка движков");
            teardown();
            NzBox.LOG.add(NzLog.NZ, "движки остановлены");
        }
        finishStopped(reason, startId);
    }

    /** Погасить всё, что поднято, в обратном порядке. Не бросает. */
    private void teardown() {
        NzCommands c = commands;
        commands = null;
        if (c != null) {
            try {
                c.close();
            } catch (Throwable ignored) {
            }
        }
        unregisterIdle();
        NzPlatform p = platform;
        if (p != null) {
            p.closeTun();
        }
        NzNetworkMonitor m = monitor;
        monitor = null;
        if (m != null) {
            try {
                m.stop();
            } catch (Throwable ignored) {
            }
        }
        closeServer();
        platform = null;
        NzByeDpi b = byedpi;
        byedpi = null;
        dpi = null;
        if (b != null) {
            try {
                b.stop();
            } catch (Throwable ignored) {
            }
        }
        NzRoot r = root;
        root = null;
        if (r != null) {
            try {
                r.stop();
            } catch (Throwable ignored) {
            }
        }
        engineUp = false;
        request = null;
        configDigest = null;
    }

    private void closeServer() {
        final CommandServer s = server;
        server = null;
        if (s == null) {
            return;
        }
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                // Сперва сервер команд (grpcServer.Stop и слушатель), потом экземпляр. Закрывая
                // UnixListener, Go стирает command.sock по имени, не сверяя, чей там сокет
                // (net/unixsock_posix.go, unlink), а имя у всех сеансов одно: поздний close()
                // после долгого closeService стёр бы сокет следующего сеанса (разбор 27.10).
                // CloseService флага closed не смотрит и экземпляр закроет и после close()
                // (daemon/started_service.go); подписчики после Close молча глотают события.
                try {
                    s.close();
                } catch (Throwable ignored) {
                }
                try {
                    s.closeService();
                } catch (Throwable e) {
                    NzBox.LOG.add(NzLog.NZ, "libbox: закрытие: " + NzSecrets.describe(e));
                }
                closed(Thread.currentThread());
            }
        }, "nz-libbox-close");
        t.setDaemon(true);
        closer = t;
        t.start();
        try {
            t.join(CLOSE_WAIT_MS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        if (t.isAlive()) {
            // Компонент sing-box закрывается без срока (adapter/lifecycle.go: после 5 с только предупреждение).
            NzBox.LOG.add(NzLog.NZ, "libbox не закрылся за " + (CLOSE_WAIT_MS / 1000)
                    + " с — очередь идёт дальше, новый запуск подождёт его");
        }
    }

    /** Поток закрытия кончил: в очереди снять «ещё закрывается» и записать «остановлен». */
    private static void closed(final Thread which) {
        NzBox.GATE.execute(new Runnable() {
            @Override
            public void run() {
                if (closer == which) {
                    closer = null;
                }
                if (!closeNotice) {
                    return;
                }
                closeNotice = false;
                String reason = closeReason;
                closeReason = null;
                if (NzBox.STOPPING.equals(NzBox.state())) {
                    NzBox.LOG.add(NzLog.NZ, "прежний движок закрылся");
                    NzBox.setState(NzBox.STOPPED, reason);
                }
            }
        });
    }

    private static boolean closing() {
        Thread t = closer;
        return t != null && t.isAlive();
    }

    /**
     * Ждать, пока докроется прежний libbox; false — не дождались за
     * PREVIOUS_CLOSE_WAIT_MS или остановку попросили раньше. Опрос по 250 мс,
     * чтобы «Остановить» не ждало все 30 с.
     */
    private static boolean awaitPreviousClose() {
        Thread t = closer;
        if (t == null || !t.isAlive()) {
            return true;
        }
        NzBox.LOG.add(NzLog.NZ, "жду, пока закроется прежний движок");
        long deadline = SystemClock.elapsedRealtime() + PREVIOUS_CLOSE_WAIT_MS;
        while (t.isAlive() && NzBox.desired()) {
            long left = deadline - SystemClock.elapsedRealtime();
            if (left <= 0) {
                break;
            }
            try {
                t.join(Math.min(left, 250));
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            }
        }
        return !t.isAlive();
    }

    /**
     * «Остановлен» — только когда libbox правда закрылся. Пока поток закрытия
     * жив, Go держит свою копию TUN и сервер команд: «stopped» соврал бы, и
     * C# (StopAsync, затем StartAsync при перезапуске) поднимал бы новый
     * движок поверх. Тогда — «stopping» с объяснением, а «stopped» с причиной
     * пишет {@link #closed}.
     */
    private static void markStopped(String reason) {
        if (closing()) {
            closeNotice = true;
            closeReason = reason;
            NzBox.LOG.add(NzLog.NZ, "libbox ещё закрывается — «остановлен» будет, когда закроется");
            NzBox.setState(NzBox.STOPPING, reason != null ? STILL_CLOSING + ": " + reason : STILL_CLOSING);
        } else {
            NzBox.setState(NzBox.STOPPED, reason);
        }
    }

    private void finishStopped(String reason, int startId) {
        markStopped(reason);
        release(startId);
    }

    /**
     * Записать итог и погасить службу — в главном потоке, где идёт и
     * onStartCommand: проверка «не было ли запуска новее» и снятие переднего
     * плана тогда не разойдутся с пришедшим запуском.
     */
    private void finish(String state, String error, final int startId) {
        NzBox.setState(state, error);
        release(startId);
    }

    private void release(final int startId) {
        main.post(new Runnable() {
            @Override
            public void run() {
                if (startId < lastStartId) {
                    // После нас пришёл новый запуск — он уже в очереди и сам решит, жить ли службе.
                    return;
                }
                leaveForeground();
                stopSelf(startId);
            }
        });
    }

    private static boolean same(String a, String b) {
        return a == null ? b == null : a.equals(b);
    }

    // ================= сон устройства =================

    /** Doze: libbox сам закрывает простаивающие соединения и будится (как BoxService у SFA). */
    private void registerIdle() {
        if (idleReceiver != null) {
            return;
        }
        BroadcastReceiver receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
                if (pm == null) {
                    return;
                }
                final boolean idle = pm.isDeviceIdleMode();
                NzBox.GATE.execute(new Runnable() {
                    @Override
                    public void run() {
                        CommandServer s = server;
                        if (s == null) {
                            return;
                        }
                        try {
                            if (idle) {
                                s.pause();
                            } else {
                                s.wake();
                            }
                        } catch (Throwable ignored) {
                        }
                    }
                });
            }
        };
        IntentFilter filter = new IntentFilter(PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED);
        try {
            if (Build.VERSION.SDK_INT >= 33) {
                registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
            } else {
                registerReceiver(receiver, filter);
            }
            idleReceiver = receiver;
        } catch (Throwable e) {
            NzBox.LOG.add(NzLog.NZ, "сон устройства не отслеживается: " + NzSecrets.describe(e));
        }
    }

    private void unregisterIdle() {
        BroadcastReceiver receiver = idleReceiver;
        idleReceiver = null;
        if (receiver != null) {
            try {
                unregisterReceiver(receiver);
            } catch (Throwable ignored) {
            }
        }
    }

    // ================= уведомление =================

    private boolean enterForeground() {
        try {
            Notification n = buildNotification();
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
            } else {
                startForeground(NOTIFICATION_ID, n);
            }
            foreground = true;
            return true;
        } catch (Throwable t) {
            NzBox.LOG.add(NzLog.NZ, "startForeground: " + NzSecrets.describe(t));
            return false;
        }
    }

    private void leaveForeground() {
        if (!foreground) {
            return;
        }
        foreground = false;
        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable ignored) {
        }
    }

    void refreshNotification() {
        if (!foreground) {
            return;
        }
        try {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) {
                nm.notify(NOTIFICATION_ID, buildNotification());
            }
        } catch (Throwable ignored) {
        }
    }

    private Notification buildNotification() {
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26 && nm != null && nm.getNotificationChannel(CHANNEL_ID) == null) {
            NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "Работа NetZapret",
                    NotificationManager.IMPORTANCE_LOW);
            channel.setShowBadge(false);
            nm.createNotificationChannel(channel);
        }
        Notification.Builder b = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this).setPriority(Notification.PRIORITY_LOW);
        int icon = smallIcon(this);
        b.setSmallIcon(icon)
                .setContentTitle(NzBox.sessionTitle())
                .setContentText(NzBox.notificationText())
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setShowWhen(false)
                .setCategory(Notification.CATEGORY_SERVICE);
        if (Build.VERSION.SDK_INT >= 31) {
            // Без этого Android 12+ показывает уведомление службы с задержкой до 10 с.
            b.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE);
        }
        int flags = PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT;
        Intent open = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (open != null) {
            b.setContentIntent(PendingIntent.getActivity(this, 0, open, flags));
        }
        Intent stop = new Intent(this, NzVpnService.class).setAction(ACTION_STOP);
        PendingIntent stopIntent = PendingIntent.getService(this, 1, stop, flags);
        b.addAction(new Notification.Action.Builder(Icon.createWithResource(this, icon), "Остановить", stopIntent).build());
        return b.build();
    }

    /**
     * Значок уведомления: одноцветный ic_tile приложения (Resources/drawable, WP4);
     * по имени, а не через R — Java-часть не знает пакета R приложения.
     */
    static int smallIcon(Context context) {
        try {
            int id = context.getResources().getIdentifier("ic_tile", "drawable", context.getPackageName());
            if (id != 0) {
                return id;
            }
        } catch (Throwable ignored) {
        }
        return android.R.drawable.ic_lock_lock;
    }

    // ================= сервер команд → служба =================

    /**
     * Обратные вызовы сервера команд (с потоков Go). Ничего не ждут:
     * остановка и перезагрузка уходят в очередь, иначе Go ждал бы саму
     * очередь, а та — Go.
     */
    private final class ServerHandler implements CommandServerHandler {
        @Override
        public void serviceStop() {
            NzBox.setDesired(false);
            requestStop(null);
        }

        @Override
        public void serviceReload() {
            NzBox.GATE.execute(new Runnable() {
                @Override
                public void run() {
                    startEngine(lastStartId);
                }
            });
        }

        @Override
        public SystemProxyStatus getSystemProxyStatus() {
            // Системный HTTP-прокси через VPN не включаем: приложения с ним ходят в обход правил.
            SystemProxyStatus status = new SystemProxyStatus();
            status.setAvailable(false);
            status.setEnabled(false);
            return status;
        }

        @Override
        public void setSystemProxyEnabled(boolean enabled) {
        }

        @Override
        public void triggerNativeCrash() throws Exception {
            throw new Exception("не поддерживается");
        }

        @Override
        public void writeDebugMessage(String message) {
            try {
                NzBox.LOG.add(NzLog.NZ, "libbox: " + message);
            } catch (Throwable ignored) {
            }
        }

        @Override
        public int connectSSHAgent() throws Exception {
            throw new Exception("SSH-агент не поддерживается");
        }
    }
}
