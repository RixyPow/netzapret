package org.netzapret.android;

import android.content.Context;
import android.os.SystemClock;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.BindException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.security.SecureRandom;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * ByeDPI — десинк без root (DESIGN.md: аналог winws2). Исполняемый
 * libbyedpi.so из nativeLibraryDir, отдельным процессом под надзором, как
 * движки Windows-версии под SupervisedService: глобальное состояние ByeDPI
 * между запусками не сбрасывается, а его падение и его signal() не должны
 * задевать процесс с VPN и .NET (byedpi.md §2.4).
 *
 * Запускается до establish(): тогда у ребёнка нет дескриптора TUN, который
 * он мог бы унаследовать. Перезапуски после подъёма VPN идут через
 * ProcessBuilder — он закрывает в ребёнке все дескрипторы выше 2.
 *
 * Порт на 127.0.0.1 открыт любому приложению телефона, и занять его может
 * любое: ByeDPI тогда не встанет (bind), а sing-box понесёт чужому слушателю
 * пароль и все соединения «десинка». Поэтому порт и пароль — свои на каждый
 * подъём (служба, {@link #freePort}), между перезапусками порт держит сам
 * надзор ({@link #holdPort}), а занятый чужим порт — не перезапуск, а переезд
 * на другой порт с перезагрузкой конфига ({@link PortLoss}).
 */
final class NzByeDpi {
    /** Кому сказать, что порт ByeDPI слушает чужая программа: службе — переезжать. */
    interface PortLoss {
        void portLost(NzByeDpi which);
    }

    // Паузы перед перезапуском, как у надзора Windows: часто падающий процесс не крутится вхолостую.
    private static final long[] BACKOFF_MS = {500, 1000, 2000, 5000};
    // Проживший дольше этого процесс считается здоровым — следующая пауза снова с начала.
    private static final long STABLE_MS = 60_000;
    private static final int RING = 200;
    // Не 1080 и не другие известные порты прокси: их проверяют первыми (byedpi.md §5.2).
    private static final int PORT_FROM = 30000;
    private static final int PORT_TO = 61000;
    private static final SecureRandom RANDOM = new SecureRandom();

    private final Context context;
    private final NzRequest.ByeDpi spec;
    private final PortLoss loss;
    // Свои 200 строк: в общем журнале их вытеснили бы строки движка.
    private final ArrayDeque<String> ring = new ArrayDeque<>();
    private String last;
    private final Object wake = new Object();

    private volatile Process process;
    private volatile boolean stopping;
    private volatile boolean ready;
    private volatile int restarts;
    private volatile String lastError;
    private volatile long spawnedAt;
    private Thread supervisor;

    /** {@code spec} — уже со своими портом и паролем ({@link NzRequest.ByeDpi#withSession}). */
    NzByeDpi(Context context, NzRequest.ByeDpi spec, PortLoss loss) {
        this.context = context.getApplicationContext() != null ? context.getApplicationContext() : context;
        this.spec = spec;
        this.loss = loss;
    }

    int port() {
        return spec.port;
    }

    /**
     * Запустить первый процесс и надзор за ним. Не бросает: без ByeDPI VPN
     * всё равно поднимается, а причина остаётся в status().byedpi.lastError.
     */
    void start() {
        if (spec.auth != null) {
            NzSecrets.register(spec.auth);
            int colon = spec.auth.indexOf(':');
            if (colon > 0) {
                NzSecrets.register(spec.auth.substring(colon + 1));
            }
        }
        final File exe = executable();
        if (!exe.isFile()) {
            // Ошибка сборки APK — перезапуски тут не помогут.
            lastError = "нет " + exe + " — APK собран без ByeDPI или без extractNativeLibs=\"true\"";
            NzBox.LOG.add(NzLog.NZ, lastError);
            return;
        }
        try {
            spawn(exe);
        } catch (IOException e) {
            // Надзор ниже попробует снова с паузой.
            process = null;
            lastError = "ByeDPI не запустился: " + NzSecrets.describe(e);
            NzBox.LOG.add(NzLog.NZ, lastError);
        }
        supervisor = new Thread(new Runnable() {
            @Override
            public void run() {
                supervise(exe);
            }
        }, "nz-byedpi");
        supervisor.setDaemon(true);
        supervisor.start();
    }

    private File executable() {
        return new File(context.getApplicationInfo().nativeLibraryDir, "libbyedpi.so");
    }

    private void spawn(File exe) throws IOException {
        List<String> command = new ArrayList<>(spec.args.size() + 1);
        command.add(exe.getAbsolutePath());
        command.addAll(spec.args);
        ProcessBuilder builder = new ProcessBuilder(command).redirectErrorStream(true);
        if (spec.workDir != null) {
            File dir = new File(spec.workDir);
            if (!dir.isDirectory()) {
                dir.mkdirs();
            }
            builder.directory(dir);
        }
        Map<String, String> env = builder.environment();
        // Пароль — переменной окружения, не в argv: argv виден в /proc/<pid>/cmdline (byedpi.md §5.3).
        env.remove("NZ_BYEDPI_AUTH");
        if (spec.auth != null) {
            env.put("NZ_BYEDPI_AUTH", spec.auth);
        }
        ready = false;
        Process p = builder.start();
        try {
            p.getOutputStream().close();
        } catch (IOException ignored) {
        }
        spawnedAt = SystemClock.elapsedRealtime();
        process = p;
        NzBox.LOG.add(NzLog.BYEDPI, "запуск: порт " + spec.port + ", ключей " + spec.args.size());
        NzProc.pump(p.getInputStream(), new NzProc.LineSink() {
            @Override
            public void line(String line) {
                remember(line);
            }
        }, "nz-byedpi-out");
    }

    private void remember(String line) {
        String clean = NzSecrets.scrub(line);
        synchronized (ring) {
            ring.addLast(String.format(Locale.ROOT, "%tT %s", System.currentTimeMillis(), clean));
            while (ring.size() > RING) {
                ring.removeFirst();
            }
            last = clean;
        }
        NzBox.LOG.add(NzLog.BYEDPI, clean);
    }

    private String lastLine() {
        synchronized (ring) {
            return last;
        }
    }

    private void supervise(File exe) {
        int failures = 0;
        while (!stopping) {
            Process p = process;
            if (p != null) {
                int code = waitExit(p);
                ready = false;
                if (stopping) {
                    break;
                }
                if (SystemClock.elapsedRealtime() - spawnedAt > STABLE_MS) {
                    failures = 0;
                }
                String tail = lastLine();
                lastError = "ByeDPI завершился с кодом " + code + (tail != null ? ": " + tail : "");
                restarts++;
                NzBox.LOG.add(NzLog.NZ, lastError);
                NzBox.changed();
            }
            // Пока нашего ByeDPI нет, порт держим сами: иначе чужая программа, ждущая
            // его освобождения, заняла бы порт в паузе, и sing-box понёс бы ей соединения.
            // Не вышло занять — его уже слушает кто-то другой (наш ребёнок мёртв): переезд.
            ServerSocket hold;
            try {
                hold = holdPort(spec.port);
            } catch (BindException taken) {
                if (!stopping) {
                    losePort();
                }
                return;
            } catch (IOException e) {
                hold = null;
                NzBox.LOG.add(NzLog.NZ, "порт ByeDPI не удержан на время паузы: " + NzSecrets.describe(e));
            }
            long delay = BACKOFF_MS[Math.min(failures, BACKOFF_MS.length - 1)];
            failures++;
            boolean resume;
            try {
                resume = pause(delay);
            } finally {
                // Отпустить прямо перед запуском: ByeDPI займёт порт через свой SO_REUSEADDR.
                release(hold);
            }
            if (!resume) {
                break;
            }
            try {
                spawn(exe);
                NzBox.LOG.add(NzLog.NZ, "ByeDPI перезапущен (" + restarts + "-й раз)");
                // Готовность после перезапуска — для status(): sing-box ждать её не может.
                waitReady(5000);
                NzBox.changed();
            } catch (IOException e) {
                process = null;
                lastError = "ByeDPI не запустился: " + NzSecrets.describe(e);
                NzBox.LOG.add(NzLog.NZ, lastError);
            }
        }
    }

    private static int waitExit(Process p) {
        while (true) {
            try {
                return p.waitFor();
            } catch (InterruptedException e) {
                // надзор прерывают только при остановке — тогда процесс уже гасится
                if (NzProc.exited(p)) {
                    return p.exitValue();
                }
            }
        }
    }

    private boolean pause(long millis) {
        synchronized (wake) {
            long deadline = SystemClock.elapsedRealtime() + millis;
            while (!stopping) {
                long left = deadline - SystemClock.elapsedRealtime();
                if (left <= 0) {
                    return true;
                }
                try {
                    wake.wait(left);
                } catch (InterruptedException e) {
                    return !stopping;
                }
            }
            return false;
        }
    }

    /**
     * Ждать, пока ByeDPI начнёт отвечать: подключение к 127.0.0.1:порт
     * и рукопожатие SOCKS5 с паролем, раз в 50 мс до срока (как awaitProxy
     * у ByeByeDPI). false — не ответил или вышел.
     */
    boolean waitReady(long timeoutMillis) {
        long deadline = SystemClock.elapsedRealtime() + timeoutMillis;
        while (!stopping) {
            Process p = process;
            if (p == null || NzProc.exited(p)) {
                ready = false;
                return false;
            }
            if (probe()) {
                // Ответ при уже вышедшем ребёнке дал не он (bind не удался, порт чужой).
                if (NzProc.exited(p)) {
                    ready = false;
                    return false;
                }
                ready = true;
                return true;
            }
            if (SystemClock.elapsedRealtime() >= deadline) {
                break;
            }
            try {
                Thread.sleep(50);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            }
        }
        ready = false;
        return false;
    }

    private boolean probe() {
        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), spec.port), 250);
            socket.setSoTimeout(500);
            OutputStream out = socket.getOutputStream();
            InputStream in = socket.getInputStream();
            if (spec.auth == null) {
                out.write(new byte[]{5, 1, 0});
                out.flush();
                return readPair(in, 5, 0);
            }
            // RFC 1929: предлагаем только 0x02 — на «без пароля» наш ByeDPI молчит и закрывает.
            out.write(new byte[]{5, 1, 2});
            out.flush();
            if (!readPair(in, 5, 2)) {
                return false;
            }
            int colon = spec.auth.indexOf(':');
            byte[] user = spec.auth.substring(0, colon).getBytes(NzRequest.UTF8);
            byte[] pass = spec.auth.substring(colon + 1).getBytes(NzRequest.UTF8);
            if (user.length > 255 || pass.length > 255) {
                return false;
            }
            byte[] packet = new byte[3 + user.length + pass.length];
            packet[0] = 1;
            packet[1] = (byte) user.length;
            System.arraycopy(user, 0, packet, 2, user.length);
            packet[2 + user.length] = (byte) pass.length;
            System.arraycopy(pass, 0, packet, 3 + user.length, pass.length);
            out.write(packet);
            out.flush();
            return readPair(in, 1, 0);
        } catch (IOException e) {
            return false;
        } finally {
            try {
                socket.close();
            } catch (IOException ignored) {
            }
        }
    }

    private static boolean readPair(InputStream in, int first, int second) throws IOException {
        int a = in.read();
        int b = in.read();
        return a == first && b == second;
    }

    private void losePort() {
        lastError = "порт " + spec.port + " на 127.0.0.1 занят другой программой — ByeDPI переезжает на другой порт";
        NzBox.LOG.add(NzLog.NZ, lastError);
        NzBox.changed();
        if (loss != null) {
            loss.portLost(this);
        }
    }

    // ================= порт и пароль сессии =================

    /** Случайный свободный порт 30000–60999; 0 — за 50 попыток свободного не нашлось. */
    static int freePort() {
        for (int attempt = 0; attempt < 50; attempt++) {
            int port = PORT_FROM + RANDOM.nextInt(PORT_TO - PORT_FROM);
            if (portFree(port)) {
                return port;
            }
        }
        return 0;
    }

    /**
     * Пробная привязка с SO_REUSEADDR, как у самого ByeDPI: не мешают
     * TIME_WAIT прежних соединений, мешает только чужой слушатель.
     */
    static boolean portFree(int port) {
        ServerSocket probe = null;
        try {
            probe = new ServerSocket();
            probe.setReuseAddress(true);
            probe.bind(new InetSocketAddress(loopback(), port), 1);
            return true;
        } catch (IOException e) {
            return false;
        } finally {
            release(probe);
        }
    }

    /** Пароль SOCKS5 на сессию: 24 случайных байта, base64url без «=» — ни «:», ни «/» в нём нет. */
    static String newPassword() {
        byte[] bytes = new byte[24];
        RANDOM.nextBytes(bytes);
        return Base64.encodeToString(bytes, Base64.URL_SAFE | Base64.NO_PADDING | Base64.NO_WRAP);
    }

    /**
     * Слушать порт самим, пока ByeDPI перезапускается: каждое соединение
     * сразу сбрасывается (RST, без TIME_WAIT на этом порту), так что sing-box
     * получает отказ, как от пустого порта, а чужой слушатель встать не может.
     * BindException — порт уже слушает кто-то другой.
     */
    private static ServerSocket holdPort(int port) throws IOException {
        final ServerSocket server = new ServerSocket();
        try {
            server.setReuseAddress(true);
            server.bind(new InetSocketAddress(loopback(), port), 16);
        } catch (IOException e) {
            release(server);
            throw e;
        }
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                while (!server.isClosed()) {
                    try {
                        Socket s = server.accept();
                        try {
                            s.setSoLinger(true, 0);
                        } catch (IOException ignored) {
                        }
                        try {
                            s.close();
                        } catch (IOException ignored) {
                        }
                    } catch (IOException e) {
                        if (server.isClosed()) {
                            return;
                        }
                        try {
                            Thread.sleep(50);
                        } catch (InterruptedException ie) {
                            return;
                        }
                    }
                }
            }
        }, "nz-byedpi-hold");
        t.setDaemon(true);
        t.start();
        return server;
    }

    private static void release(ServerSocket server) {
        if (server != null) {
            try {
                server.close();
            } catch (IOException ignored) {
            }
        }
    }

    private static InetAddress loopback() throws IOException {
        return InetAddress.getByAddress(new byte[]{127, 0, 0, 1});
    }

    /** SIGTERM, через 2 с — SIGKILL; перезапусков после этого нет. */
    void stop() {
        stopping = true;
        synchronized (wake) {
            wake.notifyAll();
        }
        Process p = process;
        if (p != null) {
            NzProc.terminate(p, 2000);
        }
        Thread t = supervisor;
        if (t != null) {
            t.interrupt();
            try {
                t.join(1000);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }
        ready = false;
        NzBox.LOG.add(NzLog.NZ, "ByeDPI остановлен");
    }

    boolean running() {
        Process p = process;
        return p != null && !stopping && !NzProc.exited(p);
    }

    List<String> lines() {
        synchronized (ring) {
            return new ArrayList<>(ring);
        }
    }

    JSONObject status() throws JSONException {
        JSONObject o = new JSONObject();
        o.put("running", running());
        o.put("ready", ready);
        o.put("restarts", restarts);
        o.put("lastError", lastError == null ? JSONObject.NULL : lastError);
        o.put("port", spec.port);
        JSONArray tail = new JSONArray();
        List<String> all = lines();
        for (int i = Math.max(0, all.size() - 20); i < all.size(); i++) {
            tail.put(all.get(i));
        }
        o.put("log", tail);
        return o;
    }
}
