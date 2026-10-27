package org.netzapret.android;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.util.List;

/**
 * Команды root для nfqws2 (DESIGN.md: десинк с root, по желанию). Что
 * запускать, решает C# (Nfqws2CommandLine, NfqueueFirewall — WP2): сюда
 * приходят готовые строки вроде ["su","-c","sh …/nfqws2-start.sh"].
 * Готовность nfqws2 (pid-файл) проверяет хозяин; здесь только запуск,
 * остановка и вывод команд в журнал.
 */
final class NzRoot {
    // su при первом запросе показывает окно Magisk — человеку нужно время нажать «Разрешить».
    private static final long START_WAIT_MS = 20_000;
    private static final long STOP_WAIT_MS = 10_000;

    private final NzRequest.Root spec;
    private volatile Process startProcess;
    private volatile boolean running;
    private volatile String lastError;

    NzRoot(NzRequest.Root spec) {
        this.spec = spec;
    }

    /**
     * Выполнить команду запуска. Сценарий nfqws2 (Nfqws2CommandLine.StartScript)
     * печатает «NZ_PID n» и делает exec nfqws2 — команда su живёт, пока живёт
     * nfqws2: эта строка и есть готовность. «NZ_FAIL …» — отказ до запуска.
     * Вышла с 0 без строки — разовая команда, поднято; с другим кодом —
     * ошибка; ни строки, ни выхода за 20 с — считаем долгоживущей.
     * Исключений нет: без root VPN всё равно поднимается, а маршрут
     * «десинк» тогда просто идёт напрямую.
     */
    void start() {
        try {
            Process p = new ProcessBuilder(spec.start).redirectErrorStream(true).start();
            try {
                p.getOutputStream().close();
            } catch (IOException ignored) {
            }
            startProcess = p;
            // Без этой строки ждали бы все 20 с: exec держит su до остановки nfqws2.
            final java.util.concurrent.CountDownLatch marker = new java.util.concurrent.CountDownLatch(1);
            final String[] seen = new String[1];
            NzProc.pump(p.getInputStream(), new NzProc.LineSink() {
                @Override
                public void line(String line) {
                    NzBox.LOG.add(NzLog.ROOT, line);
                    if (seen[0] == null && (line.startsWith("NZ_PID ") || line.startsWith("NZ_FAIL"))) {
                        seen[0] = line;
                        marker.countDown();
                    }
                }
            }, "nz-root-start");
            boolean marked = false;
            long deadline = System.currentTimeMillis() + START_WAIT_MS;
            while (System.currentTimeMillis() < deadline) {
                try {
                    if (marker.await(200, java.util.concurrent.TimeUnit.MILLISECONDS)) {
                        marked = true;
                        break;
                    }
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    break;
                }
                // Не p.isAlive(): он с API 26, а minSdk 24 — на Android 7 вызов падал
                // NoSuchMethodError, и с root не поднимался весь VPN (разбор 27.10).
                if (NzProc.exited(p)) {
                    break;
                }
            }
            if (marked && seen[0].startsWith("NZ_PID ")) {
                running = true;
                lastError = null;
                NzBox.LOG.add(NzLog.NZ, "root: nfqws2 запущен");
                return;
            }
            if (marked) {
                running = false;
                lastError = "root: nfqws2 не запущен — " + seen[0].substring("NZ_FAIL".length()).trim();
                NzBox.LOG.add(NzLog.NZ, lastError);
                return;
            }
            if (NzProc.waitFor(p, 200)) {
                int code = p.exitValue();
                if (code == 0) {
                    running = true;
                    lastError = null;
                    NzBox.LOG.add(NzLog.NZ, "root: запуск выполнен");
                } else {
                    running = false;
                    lastError = "команда запуска root завершилась с кодом " + code;
                    NzBox.LOG.add(NzLog.NZ, lastError);
                }
            } else {
                running = true;
                lastError = null;
                NzBox.LOG.add(NzLog.NZ, "root: команда запуска работает дольше " + (START_WAIT_MS / 1000)
                        + " с — считаю её долгоживущей");
            }
        } catch (IOException e) {
            running = false;
            // «su» не найден — root нет; текст ошибки Java называет только файл.
            lastError = "root недоступен: " + NzSecrets.describe(e);
            NzBox.LOG.add(NzLog.NZ, lastError);
        } catch (RuntimeException | LinkageError e) {
            // Обещание «исключений нет» — и для непредвиденного: сбой root не должен
            // ронять подъём VPN (fullStart ловит Throwable и гасит всё).
            running = false;
            lastError = "root: сбой запуска — " + NzSecrets.describe(e);
            NzBox.LOG.add(NzLog.NZ, lastError);
        }
    }

    /** Команда остановки (снимает правила iptables и гасит nfqws2), затем процесс запуска. */
    void stop() {
        List<String> stop = spec.stop;
        if (!stop.isEmpty()) {
            try {
                Process p = new ProcessBuilder(stop).redirectErrorStream(true).start();
                try {
                    p.getOutputStream().close();
                } catch (IOException ignored) {
                }
                NzProc.pump(p.getInputStream(), new NzProc.LineSink() {
                    @Override
                    public void line(String line) {
                        NzBox.LOG.add(NzLog.ROOT, line);
                    }
                }, "nz-root-stop");
                if (NzProc.waitFor(p, STOP_WAIT_MS)) {
                    int code = p.exitValue();
                    if (code != 0) {
                        lastError = "команда остановки root завершилась с кодом " + code;
                        NzBox.LOG.add(NzLog.NZ, lastError);
                    }
                } else {
                    lastError = "команда остановки root не завершилась за " + (STOP_WAIT_MS / 1000) + " с";
                    NzBox.LOG.add(NzLog.NZ, lastError);
                    NzProc.terminate(p, 1000);
                }
            } catch (IOException e) {
                lastError = "root: остановка не запустилась: " + NzSecrets.describe(e);
                NzBox.LOG.add(NzLog.NZ, lastError);
            }
        }
        Process p = startProcess;
        if (p != null) {
            NzProc.terminate(p, 2000);
        }
        running = false;
    }

    boolean running() {
        return running;
    }

    JSONObject status() throws JSONException {
        JSONObject o = new JSONObject();
        o.put("running", running);
        o.put("lastError", lastError == null ? JSONObject.NULL : lastError);
        return o;
    }
}
