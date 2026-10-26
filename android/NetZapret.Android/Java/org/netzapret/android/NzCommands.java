package org.netzapret.android;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import io.nekohasekai.libbox.CommandClient;
import io.nekohasekai.libbox.CommandClientHandler;
import io.nekohasekai.libbox.CommandClientOptions;
import io.nekohasekai.libbox.Connection;
import io.nekohasekai.libbox.ConnectionEvents;
import io.nekohasekai.libbox.ConnectionIterator;
import io.nekohasekai.libbox.Connections;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LogEntry;
import io.nekohasekai.libbox.LogIterator;
import io.nekohasekai.libbox.OutboundGroup;
import io.nekohasekai.libbox.OutboundGroupItem;
import io.nekohasekai.libbox.OutboundGroupItemIterator;
import io.nekohasekai.libbox.OutboundGroupIterator;
import io.nekohasekai.libbox.ProcessInfo;
import io.nekohasekai.libbox.StatusMessage;
import io.nekohasekai.libbox.StringIterator;

/**
 * Управление движком через сервер команд libbox (Unix-сокет в папке
 * приложения) — на телефоне вместо Clash API: слушателя на 127.0.0.1 без
 * пароля нет (DESIGN.md). Один постоянный клиент держит журнал, группы
 * и режим (события, без таймеров — батарея); разовые действия —
 * отдельным «standalone»-клиентом на каждый вызов, как у SFA (libbox.md §4.3).
 *
 * Все методы CommandClientHandler в Go без error: ни один не бросает.
 */
final class NzCommands {
    private static final long SECOND_NS = 1_000_000_000L;

    private final Object lock = new Object();
    private volatile boolean closed = true;
    private Session session;

    // Снимок групп — строкой JSON: готов к отдаче, итераторы Go живут только внутри обратного вызова.
    private String groupsJson;
    private Map<String, Integer> groupSizes = Collections.emptyMap();
    private volatile String clashMode = "";
    private volatile List<String> clashModes = Collections.emptyList();

    void open() {
        synchronized (lock) {
            closed = false;
        }
        connectLater(0);
    }

    void close() {
        Session s;
        synchronized (lock) {
            closed = true;
            s = session;
            session = null;
            groupsJson = null;
            groupSizes = Collections.emptyMap();
            lock.notifyAll();
        }
        if (s != null) {
            s.disconnect();
        }
        clashMode = "";
        clashModes = Collections.emptyList();
    }

    private void connectLater(final long delayMillis) {
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                if (delayMillis > 0) {
                    try {
                        Thread.sleep(delayMillis);
                    } catch (InterruptedException e) {
                        return;
                    }
                }
                connectLoop();
            }
        }, "nz-commands");
        t.setDaemon(true);
        t.start();
    }

    private void connectLoop() {
        // connect() сам ждёт сервер до ~3 с; здесь — повтор, если сервер ещё поднимался дольше.
        for (int attempt = 0; attempt < 10; attempt++) {
            if (closed) {
                return;
            }
            Session s = new Session();
            try {
                CommandClientOptions options = new CommandClientOptions();
                options.addCommand(Libbox.CommandLog);
                options.addCommand(Libbox.CommandGroup);
                options.addCommand(Libbox.CommandClashMode);
                options.setStatusInterval(SECOND_NS);
                s.client = new CommandClient(s, options);
                s.client.connect();
            } catch (Throwable e) {
                s.retired = true;
                NzBox.LOG.add(NzLog.NZ, "сервер команд не ответил: " + NzSecrets.describe(e));
                try {
                    Thread.sleep(1000);
                } catch (InterruptedException ie) {
                    return;
                }
                continue;
            }
            Session old;
            synchronized (lock) {
                if (closed) {
                    s.retired = true;
                    old = null;
                } else {
                    old = session;
                    session = s;
                }
            }
            if (old != null) {
                old.disconnect();
            }
            if (s.retired) {
                s.disconnect();
            }
            return;
        }
        NzBox.LOG.add(NzLog.NZ, "журнал и группы движка недоступны: сервер команд не отвечает");
    }

    /**
     * Связь одного клиента. Обрыв любого потока зовёт disconnected(), иногда
     * по разу на поток; переподключается только текущая связь и только раз —
     * иначе отключение старой при подмене вызывало бы новые подмены.
     */
    private final class Session implements CommandClientHandler {
        volatile CommandClient client;
        volatile boolean retired;

        void disconnect() {
            retired = true;
            CommandClient c = client;
            if (c != null) {
                try {
                    c.disconnect();
                } catch (Throwable ignored) {
                }
            }
        }

        @Override
        public void connected() {
        }

        @Override
        public void disconnected(String message) {
            try {
                boolean reconnect;
                synchronized (lock) {
                    reconnect = !closed && !retired && session == this;
                    if (reconnect) {
                        retired = true;
                        session = null;
                    }
                }
                if (reconnect) {
                    NzBox.LOG.add(NzLog.NZ, "связь с сервером команд прервалась (" + NzSecrets.scrub(message)
                            + ") — переподключаюсь");
                    disconnect();
                    connectLater(2000);
                }
            } catch (Throwable ignored) {
            }
        }

        @Override
        public void setDefaultLogLevel(int level) {
        }

        @Override
        public void clearLogs() {
            try {
                if (!retired) {
                    NzBox.LOG.clear(NzLog.ENGINE);
                }
            } catch (Throwable ignored) {
            }
        }

        @Override
        public void writeLogs(LogIterator messages) {
            try {
                while (messages != null && messages.hasNext()) {
                    LogEntry e = messages.next();
                    if (e != null && !retired) {
                        NzBox.LOG.add(NzLog.ENGINE, NzLog.level(e.getLevel()) + " " + e.getMessage());
                    }
                }
            } catch (Throwable ignored) {
            }
        }

        @Override
        public void writeStatus(StatusMessage message) {
        }

        @Override
        public void writeGroups(OutboundGroupIterator groups) {
            try {
                JSONArray array = new JSONArray();
                HashMap<String, Integer> sizes = new HashMap<>();
                while (groups != null && groups.hasNext()) {
                    OutboundGroup g = groups.next();
                    if (g == null) {
                        continue;
                    }
                    JSONObject o = new JSONObject();
                    o.put("tag", g.getTag());
                    o.put("type", g.getType());
                    o.put("selected", g.getSelected());
                    o.put("selectable", g.getSelectable());
                    JSONArray items = new JSONArray();
                    OutboundGroupItemIterator it = g.getItems();
                    while (it != null && it.hasNext()) {
                        OutboundGroupItem item = it.next();
                        if (item == null) {
                            continue;
                        }
                        JSONObject i = new JSONObject();
                        i.put("tag", item.getTag());
                        i.put("type", item.getType());
                        // 0 — ещё не мерили; время замера — секунды Unix.
                        i.put("delay", item.getURLTestDelay());
                        i.put("time", item.getURLTestTime());
                        items.put(i);
                    }
                    o.put("items", items);
                    array.put(o);
                    sizes.put(g.getTag(), items.length());
                }
                synchronized (lock) {
                    if (!retired) {
                        groupsJson = array.toString();
                        groupSizes = sizes;
                        lock.notifyAll();
                    }
                }
            } catch (Throwable ignored) {
            }
        }

        @Override
        public void writeOutbounds(OutboundGroupItemIterator message) {
        }

        @Override
        public void initializeClashMode(StringIterator modeList, String currentMode) {
            try {
                clashModes = Collections.unmodifiableList(NzStrings.read(modeList));
                clashMode = currentMode == null ? "" : currentMode;
            } catch (Throwable ignored) {
            }
        }

        @Override
        public void updateClashMode(String newMode) {
            clashMode = newMode == null ? "" : newMode;
        }

        @Override
        public void writeConnectionEvents(ConnectionEvents events) {
        }
    }

    // ---- ответы команд ----

    /** Группы; пока первого снимка нет — ждать не дольше {@code waitMillis}. */
    String groups(long waitMillis) {
        synchronized (lock) {
            long deadline = System.currentTimeMillis() + waitMillis;
            while (groupsJson == null && !closed) {
                long left = deadline - System.currentTimeMillis();
                if (left <= 0) {
                    break;
                }
                try {
                    lock.wait(left);
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    break;
                }
            }
            return groupsJson != null ? groupsJson : "[]";
        }
    }

    /** Число серверов в группе; -1 — это не группа (отдельный сервер или неизвестно). */
    int groupSize(String tag) {
        synchronized (lock) {
            Integer size = groupSizes.get(tag);
            return size == null ? -1 : size;
        }
    }

    JSONObject clashModeStatus() throws JSONException {
        JSONObject o = new JSONObject();
        o.put("mode", clashMode);
        o.put("modes", new JSONArray(clashModes));
        return o;
    }

    static void select(String group, String outbound) throws Exception {
        Libbox.newStandaloneCommandClient().selectOutbound(group, outbound);
    }

    static void urlTest(String tag) throws Exception {
        Libbox.newStandaloneCommandClient().urlTest(tag);
    }

    static void setClashMode(String mode) throws Exception {
        Libbox.newStandaloneCommandClient().setClashMode(mode);
    }

    static void closeConnection(String id) throws Exception {
        Libbox.newStandaloneCommandClient().closeConnection(id);
    }

    static void closeConnections() throws Exception {
        Libbox.newStandaloneCommandClient().closeConnections();
    }

    /**
     * Соединения — разовым клиентом: подписка присылает первым пакетом всё
     * текущее (с пометкой «сброс»), этого и ждём; держать поток соединений
     * постоянно — таймер раз в секунду и трата батареи.
     */
    static JSONArray connections(String state, int limit) throws Exception {
        final CountDownLatch first = new CountDownLatch(1);
        final boolean[] received = {false};
        final Connections all = new Connections();
        CommandClientHandler catcher = new CommandClientHandler() {
            @Override
            public void connected() {
            }

            @Override
            public void disconnected(String message) {
                first.countDown();
            }

            @Override
            public void setDefaultLogLevel(int level) {
            }

            @Override
            public void clearLogs() {
            }

            @Override
            public void writeLogs(LogIterator messageList) {
            }

            @Override
            public void writeStatus(StatusMessage message) {
            }

            @Override
            public void writeGroups(OutboundGroupIterator message) {
            }

            @Override
            public void writeOutbounds(OutboundGroupItemIterator message) {
            }

            @Override
            public void initializeClashMode(StringIterator modeList, String currentMode) {
            }

            @Override
            public void updateClashMode(String newMode) {
            }

            @Override
            public void writeConnectionEvents(ConnectionEvents events) {
                try {
                    synchronized (all) {
                        if (first.getCount() > 0) {
                            all.applyEvents(events);
                            received[0] = true;
                            first.countDown();
                        }
                    }
                } catch (Throwable ignored) {
                    first.countDown();
                }
            }
        };
        CommandClientOptions options = new CommandClientOptions();
        options.addCommand(Libbox.CommandConnections);
        options.setStatusInterval(SECOND_NS);
        CommandClient client = new CommandClient(catcher, options);
        try {
            client.connect();
            if (!first.await(3, TimeUnit.SECONDS)) {
                throw new Exception("движок не прислал соединения за 3 с");
            }
            synchronized (all) {
                if (!received[0]) {
                    throw new Exception("сервер команд оборвал связь, не прислав соединения");
                }
            }
        } finally {
            try {
                client.disconnect();
            } catch (Throwable ignored) {
            }
        }

        JSONArray result = new JSONArray();
        synchronized (all) {
            long filter = "closed".equals(state) ? Libbox.ConnectionStateClosed
                    : "all".equals(state) ? Libbox.ConnectionStateAll : Libbox.ConnectionStateActive;
            all.filterState((int) filter);
            all.sortByDate();
            ConnectionIterator it = all.iterator();
            while (it != null && it.hasNext() && result.length() < limit) {
                Connection c = it.next();
                if (c != null) {
                    result.put(describe(c));
                }
            }
        }
        return result;
    }

    private static JSONObject describe(Connection c) throws JSONException {
        JSONObject o = new JSONObject();
        o.put("id", c.getID());
        o.put("inbound", c.getInbound());
        o.put("inboundType", c.getInboundType());
        o.put("ipVersion", c.getIPVersion());
        o.put("network", c.getNetwork());
        o.put("source", c.getSource());
        o.put("destination", c.getDestination());
        o.put("domain", c.getDomain());
        o.put("protocol", c.getProtocol());
        o.put("rule", c.getRule());
        o.put("outbound", c.getOutbound());
        o.put("outboundType", c.getOutboundType());
        JSONArray chain = new JSONArray();
        for (String hop : NzStrings.read(c.chain())) {
            chain.put(hop);
        }
        o.put("chain", chain);
        o.put("createdAt", c.getCreatedAt());
        o.put("closedAt", c.getClosedAt());
        o.put("upload", c.getUplinkTotal());
        o.put("download", c.getDownlinkTotal());
        ProcessInfo p = c.getProcessInfo();
        JSONArray packages = new JSONArray();
        if (p != null) {
            o.put("uid", p.getUserID());
            for (String pkg : NzStrings.read(p.packageNames())) {
                packages.put(pkg);
            }
        }
        o.put("packages", packages);
        return o;
    }
}
