package org.netzapret.android;

import android.os.Build;
import android.os.SystemClock;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.lang.reflect.Field;

/**
 * Дочерние процессы на API 24+. Process.waitFor(срок), isAlive и
 * destroyForcibly появились только в API 26, а minSdk у нас 24 (как
 * у libbox.aar), — отсюда опрос и номер процесса через отражение.
 */
final class NzProc {
    private NzProc() {
    }

    /** Ждать выхода не дольше {@code millis}; true — вышел. */
    static boolean waitFor(Process process, long millis) {
        long deadline = SystemClock.elapsedRealtime() + millis;
        while (true) {
            if (exited(process)) {
                return true;
            }
            long left = deadline - SystemClock.elapsedRealtime();
            if (left <= 0) {
                return false;
            }
            try {
                Thread.sleep(Math.min(left, 50));
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return exited(process);
            }
        }
    }

    static boolean exited(Process process) {
        try {
            process.exitValue();
            return true;
        } catch (IllegalThreadStateException running) {
            return false;
        }
    }

    /**
     * Мягко (SIGTERM), через {@code graceMillis} — жёстко (SIGKILL).
     * ByeDPI по SIGTERM закрывает слушатель и выходит сам (byedpi.md §1.7).
     */
    static void terminate(Process process, long graceMillis) {
        if (process == null || exited(process)) {
            return;
        }
        process.destroy();
        if (waitFor(process, graceMillis)) {
            return;
        }
        if (Build.VERSION.SDK_INT >= 26) {
            process.destroyForcibly();
        } else {
            int pid = pid(process);
            if (pid > 0) {
                android.os.Process.sendSignal(pid, android.os.Process.SIGNAL_KILL);
            }
        }
        waitFor(process, 1000);
    }

    /** Номер процесса из поля «pid» java.lang.UNIXProcess; 0 — не узнать. */
    static int pid(Process process) {
        try {
            Field f = process.getClass().getDeclaredField("pid");
            f.setAccessible(true);
            return f.getInt(process);
        } catch (Throwable e) {
            return 0;
        }
    }

    interface LineSink {
        void line(String line);
    }

    /** Читать вывод процесса строками в отдельном потоке, пока процесс не закроет его. */
    static Thread pump(final InputStream stream, final LineSink sink, String name) {
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                BufferedReader reader = new BufferedReader(new InputStreamReader(stream, NzRequest.UTF8));
                try {
                    String line;
                    while ((line = reader.readLine()) != null) {
                        try {
                            sink.line(line);
                        } catch (Throwable ignored) {
                            // приёмник журнала не должен ронять чтение
                        }
                    }
                } catch (IOException ignored) {
                    // поток закрыт — процесс вышел или его убили
                } finally {
                    try {
                        reader.close();
                    } catch (IOException ignored) {
                    }
                }
            }
        }, name);
        t.setDaemon(true);
        t.start();
        return t;
    }
}
