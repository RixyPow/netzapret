package org.netzapret.android;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;

import io.nekohasekai.libbox.InterfaceUpdateListener;

/**
 * Настоящая сеть телефона (Wi-Fi, мобильная) — не наш VPN. Нужна трём
 * местам: Go узнаёт по ней интерфейс по умолчанию (свои сокеты он
 * защищает protect() и ведёт мимо TUN), локальный DNS спрашивает её
 * резолвер, bindInterfaceControl ищет сеть по имени.
 *
 * Подписка — как у SFA (libbox.md §3.2): registerDefaultNetworkCallback
 * с Android 9 отдаёт сам VPN, поэтому на 28+ — запрос сети с INTERNET и
 * NOT_RESTRICTED (NOT_VPN NetworkRequest.Builder ставит сам), на 31+ —
 * «лучшая подходящая». Обратные вызовы — в своём потоке, не в главном.
 */
final class NzNetworkMonitor {
    private final ConnectivityManager connectivity;
    private final Object sync = new Object();
    private HandlerThread thread;
    private boolean registered;

    private volatile Network network;
    private volatile InterfaceUpdateListener listener;
    // Последнее сообщённое Go — частые onCapabilitiesChanged (сила сигнала) не дёргают его зря.
    private String lastReported;
    private String lastCapabilities;

    private final ConnectivityManager.NetworkCallback callback = new ConnectivityManager.NetworkCallback() {
        @Override
        public void onAvailable(Network n) {
            network = n;
            try {
                lastCapabilities = capabilitiesKey(connectivity.getNetworkCapabilities(n));
            } catch (Throwable e) {
                lastCapabilities = null;
            }
            report(n, true);
        }

        @Override
        public void onCapabilitiesChanged(Network n, NetworkCapabilities caps) {
            if (!n.equals(network)) {
                return;
            }
            String key = capabilitiesKey(caps);
            if (key.equals(lastCapabilities)) {
                return;
            }
            lastCapabilities = key;
            // Сменилась платность или вид сети: Go перечитает интерфейсы (Metered берётся оттуда).
            report(n, true);
        }

        @Override
        public void onLinkPropertiesChanged(Network n, LinkProperties lp) {
            if (n.equals(network)) {
                // Адреса и DNS могли смениться при том же имени — Go перечитает интерфейсы.
                report(n, true);
            }
        }

        @Override
        public void onLost(Network n) {
            if (n.equals(network)) {
                network = null;
                lastCapabilities = null;
                report(null, true);
            }
        }
    };

    NzNetworkMonitor(Context context) {
        connectivity = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
    }

    void start() {
        synchronized (sync) {
            if (registered) {
                return;
            }
            thread = new HandlerThread("nz-network");
            thread.start();
            Handler handler = new Handler(thread.getLooper());
            NetworkRequest request = new NetworkRequest.Builder()
                    .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                    .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
                    .build();
            if (Build.VERSION.SDK_INT >= 31) {
                connectivity.registerBestMatchingNetworkCallback(request, callback, handler);
            } else if (Build.VERSION.SDK_INT >= 28) {
                connectivity.requestNetwork(request, callback, handler);
            } else if (Build.VERSION.SDK_INT >= 26) {
                connectivity.registerDefaultNetworkCallback(callback, handler);
            } else {
                connectivity.registerDefaultNetworkCallback(callback);
            }
            registered = true;
        }
    }

    void stop() {
        synchronized (sync) {
            listener = null;
            if (registered) {
                try {
                    connectivity.unregisterNetworkCallback(callback);
                } catch (Throwable ignored) {
                    // уже снят системой
                }
                registered = false;
            }
            if (thread != null) {
                thread.quitSafely();
                thread = null;
            }
            network = null;
            lastReported = null;
        }
    }

    Network network() {
        return network;
    }

    ConnectivityManager connectivity() {
        return connectivity;
    }

    /**
     * Go подписывается на смену сети. Текущую сообщаем сразу, в этом же
     * вызове, — как SFA: Go ждёт её, чтобы выбрать интерфейс для своих сокетов.
     */
    void setListener(InterfaceUpdateListener l) {
        listener = l;
        synchronized (sync) {
            lastReported = null;
        }
        if (l != null) {
            report(network, true);
        }
    }

    private static String capabilitiesKey(NetworkCapabilities caps) {
        if (caps == null) {
            return "";
        }
        StringBuilder sb = new StringBuilder();
        sb.append(caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ? 'w' : '-');
        sb.append(caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) ? 'c' : '-');
        sb.append(caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) ? 'e' : '-');
        sb.append(caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) ? 'u' : 'm');
        return sb.toString();
    }

    private void report(Network n, boolean force) {
        InterfaceUpdateListener l = listener;
        if (l == null) {
            return;
        }
        String name = "";
        int index = -1;
        if (n != null) {
            // Имя и номер появляются не сразу после onAvailable — до секунды повторов, как у SFA.
            for (int attempt = 0; attempt < 10; attempt++) {
                LinkProperties lp = connectivity.getLinkProperties(n);
                String candidate = lp != null ? lp.getInterfaceName() : null;
                if (candidate != null) {
                    try {
                        java.net.NetworkInterface ni = java.net.NetworkInterface.getByName(candidate);
                        if (ni != null) {
                            name = candidate;
                            index = ni.getIndex();
                            break;
                        }
                    } catch (Throwable ignored) {
                        // интерфейс ещё не виден ядру
                    }
                }
                if (!n.equals(network)) {
                    return;
                }
                try {
                    Thread.sleep(100);
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    return;
                }
            }
            if (index < 0) {
                NzBox.LOG.add(NzLog.NZ, "сеть по умолчанию без интерфейса — Go о ней не узнает");
                return;
            }
        }
        String key = name + "#" + index;
        synchronized (sync) {
            if (!force && key.equals(lastReported)) {
                return;
            }
            lastReported = key;
        }
        try {
            l.updateDefaultInterface(name, index, false, false);
        } catch (Throwable e) {
            NzBox.LOG.add(NzLog.NZ, "смена сети не передана движку: " + NzSecrets.describe(e));
        }
    }
}
