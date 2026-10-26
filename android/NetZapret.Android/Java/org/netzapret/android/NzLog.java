package org.netzapret.android;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;

/**
 * Общий журнал телефона: строки движка (поток журнала libbox), свои шаги
 * запуска и остановки, вывод ByeDPI и команд root — в одном кольце и по
 * времени, как читает их человек. На Windows это четыре файла
 * (sing-box.log, supervisor.log, winws2.log…); здесь файлов нет: libbox
 * держит своё кольцо, а мы — своё, из которого status() и command("logs")
 * отдают хвост.
 */
final class NzLog {
    static final int ENGINE = 0;
    static final int NZ = 1;
    static final int BYEDPI = 2;
    static final int ROOT = 3;
    static final int ALL = -1;

    private static final String[] TAGS = {"движок", "nz", "byedpi", "root"};

    // Уровни sing-box (log.Level): panic, fatal, error, warn, info, debug, trace.
    private static final String[] LEVELS = {"PANIC", "FATAL", "ERROR", "WARN", "INFO", "DEBUG", "TRACE"};

    private static final class Entry {
        final long time;
        final int source;
        final String text;

        Entry(long time, int source, String text) {
            this.time = time;
            this.source = source;
            this.text = text;
        }
    }

    private final int capacity;
    private final ArrayDeque<Entry> entries = new ArrayDeque<>();

    NzLog(int capacity) {
        this.capacity = capacity;
    }

    static String level(int level) {
        return level >= 0 && level < LEVELS.length ? LEVELS[level] : "L" + level;
    }

    void add(int source, String text) {
        if (text == null) {
            return;
        }
        long now = System.currentTimeMillis();
        // Секреты вычищаются до кольца: потом строка уходит наружу без проверки.
        String clean = NzSecrets.scrub(text);
        synchronized (entries) {
            for (String line : clean.split("\r?\n")) {
                if (line.isEmpty()) {
                    continue;
                }
                entries.addLast(new Entry(now, source, line));
                while (entries.size() > capacity) {
                    entries.removeFirst();
                }
            }
        }
    }

    /**
     * Убрать строки одного источника. libbox при каждой подписке на журнал
     * присылает своё кольцо заново с пометкой «сброс» — без этого после
     * переподключения строки движка задвоились бы.
     */
    void clear(int source) {
        synchronized (entries) {
            Iterator<Entry> it = entries.iterator();
            while (it.hasNext()) {
                if (it.next().source == source) {
                    it.remove();
                }
            }
        }
    }

    /** Последние {@code max} строк источника (или всех — {@link #ALL}), старые первыми. */
    List<String> tail(int max, int source) {
        ArrayList<Entry> picked = new ArrayList<>();
        synchronized (entries) {
            Iterator<Entry> it = entries.descendingIterator();
            while (it.hasNext() && picked.size() < max) {
                Entry e = it.next();
                if (source == ALL || e.source == source) {
                    picked.add(e);
                }
            }
        }
        ArrayList<String> lines = new ArrayList<>(picked.size());
        for (int i = picked.size() - 1; i >= 0; i--) {
            Entry e = picked.get(i);
            lines.add(String.format(Locale.ROOT, "%tT [%s] %s", e.time, TAGS[e.source], e.text));
        }
        return lines;
    }
}
