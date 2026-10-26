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

import java.io.File;

import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.CommandServerHandler;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.OverrideOptions;
import io.nekohasekai.libbox.SystemProxyStatus;

/**
 * Служба VPN — на телефоне то же, что надзор на Windows: поднимает движки
 * по порядку, держит их и гасит в обратном (libbox.md §4.2, DESIGN.md).
 *
 * Порядок запуска: команда root (nfqws2) → ByeDPI и ожидание его готовности
 * (до establish: ребёнок не унаследует TUN) → Libbox.setup → сервер команд →
 * startOrReloadService. Остановка — наоборот. Всё это идёт в очереди
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
    private static final long BYEDPI_READY_MS = 5000;

    static volatile NzVpnService instance;

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
                    NzBox.setState(NzBox.STOPPED, NzBox.error());
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
        if (!NzBox.desired()) {
            // «Остановить» пришло раньше, чем служба успела начать.
            if (!engineUp) {
                finish(NzBox.STOPPED, null, startId);
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
        NzBox.LOG.add(NzLog.NZ, "запуск движков");
        try {
            String config = NzRequest.readText(new File(req.configPath));

            if (req.root != null) {
                NzRoot r = new NzRoot(req.root);
                root = r;
                r.start();
            }
            if (req.byedpi != null) {
                startByeDpi(req.byedpi);
            }
            NzLibbox.ensure(this, req.workingPath);
            if (!NzBox.desired()) {
                NzBox.LOG.add(NzLog.NZ, "запуск прерван остановкой");
                teardown();
                finish(NzBox.STOPPED, null, startId);
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
            // OverrideOptions — никогда не null (Go разыменует его, command_server.go:216).
            // AutoRedirect=false: true включил бы auto_redirect, которому нужен root и iptables nfqws2.
            OverrideOptions overrides = new OverrideOptions();
            overrides.setAutoRedirect(false);
            s.startOrReloadService(config, overrides);

            engineUp = true;
            request = req;
            configDigest = NzRequest.digest(config);
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
            String config = NzRequest.readText(new File(req.configPath));
            String digest = NzRequest.digest(config);
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
                if (b != null) {
                    b.stop();
                }
                if (req.byedpi != null) {
                    startByeDpi(req.byedpi);
                }
            }

            OverrideOptions overrides = new OverrideOptions();
            overrides.setAutoRedirect(false);
            server.startOrReloadService(config, overrides);
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

    private void startByeDpi(NzRequest.ByeDpi spec) {
        NzByeDpi b = new NzByeDpi(this, spec);
        byedpi = b;
        b.start();
        if (b.waitReady(BYEDPI_READY_MS)) {
            NzBox.LOG.add(NzLog.NZ, "ByeDPI готов на 127.0.0.1:" + spec.port);
        } else {
            // Не роняем VPN: туннель и «напрямую» работают и без десинка, а надзор
            // ByeDPI продолжит перезапуски — как winws2 на Windows («движок не работает»).
            NzBox.LOG.add(NzLog.NZ, "ByeDPI не ответил за " + (BYEDPI_READY_MS / 1000)
                    + " с — VPN поднимается без десинка, надзор перезапустит ByeDPI");
        }
    }

    private void stopEngine(String reason, int startId) {
        boolean had = engineUp || server != null || byedpi != null || root != null;
        if (had) {
            NzBox.setState(NzBox.STOPPING, null);
            NzBox.LOG.add(NzLog.NZ, reason != null ? "остановка: " + reason : "остановка движков");
            teardown();
            NzBox.LOG.add(NzLog.NZ, "движки остановлены");
        }
        finish(NzBox.STOPPED, reason, startId);
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
        Thread closer = new Thread(new Runnable() {
            @Override
            public void run() {
                try {
                    s.closeService();
                } catch (Throwable e) {
                    try {
                        s.setError("android: close service: " + NzSecrets.describe(e));
                    } catch (Throwable ignored) {
                    }
                }
                try {
                    s.close();
                } catch (Throwable ignored) {
                }
            }
        }, "nz-libbox-close");
        closer.setDaemon(true);
        closer.start();
        try {
            closer.join(CLOSE_WAIT_MS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        if (closer.isAlive()) {
            NzBox.LOG.add(NzLog.NZ, "libbox не закрылся за " + (CLOSE_WAIT_MS / 1000) + " с — продолжаю без него");
        }
    }

    /**
     * Записать итог и погасить службу — в главном потоке, где идёт и
     * onStartCommand: проверка «не было ли запуска новее» и снятие переднего
     * плана тогда не разойдутся с пришедшим запуском.
     */
    private void finish(String state, String error, final int startId) {
        NzBox.setState(state, error);
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
