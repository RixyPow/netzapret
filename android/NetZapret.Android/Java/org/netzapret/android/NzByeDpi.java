package org.netzapret.android;

import android.content.Context;
import android.os.SystemClock;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
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
 */
final class NzByeDpi {
    // Паузы перед перезапуском, как у надзора Windows: часто падающий процесс не крутится вхолостую.
    private static final long[] BACKOFF_MS = {500, 1000, 2000, 5000};
    // Проживший дольше этого процесс считается здоровым — следующая пауза снова с начала.
    private static final long STABLE_MS = 60_000;
    private static final int RING = 200;

    private final Context context;
    private final NzRequest.ByeDpi spec;
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

    NzByeDpi(Context context, NzRequest.ByeDpi spec) {
        this.context = context.getApplicationContext() != null ? context.getApplicationContext() : context;
        this.spec = spec;
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
            long delay = BACKOFF_MS[Math.min(failures, BACKOFF_MS.length - 1)];
            failures++;
            if (!pause(delay)) {
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
