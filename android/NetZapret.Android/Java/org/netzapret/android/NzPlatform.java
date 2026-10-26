package org.netzapret.android;

import android.annotation.TargetApi;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.IpPrefix;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.ProxyInfo;
import android.net.RouteInfo;
import android.net.Uri;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.system.OsConstants;

import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

import io.nekohasekai.libbox.BridgeOptions;
import io.nekohasekai.libbox.BridgeSession;
import io.nekohasekai.libbox.ConnectionOwner;
import io.nekohasekai.libbox.InterfaceUpdateListener;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LocalDNSTransport;
import io.nekohasekai.libbox.NeighborUpdateListener;
import io.nekohasekai.libbox.NetworkInterface;
import io.nekohasekai.libbox.NetworkInterfaceIterator;
import io.nekohasekai.libbox.PlatformInterface;
import io.nekohasekai.libbox.PlatformUser;
import io.nekohasekai.libbox.RoutePrefix;
import io.nekohasekai.libbox.RoutePrefixIterator;
import io.nekohasekai.libbox.ShellSession;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

/**
 * Платформа для libbox: то, что на Windows sing-box делает сам (TUN через
 * wintun, обход своих сокетов мимо туннеля, сеть по умолчанию), на Android
 * делает приложение через VpnService (libbox.md §3).
 *
 * Правило gomobile: методы, у которых в Go нет error (useProcFS, readWIFIState,
 * registerMyInterface…), не бросают никогда — висящее исключение JNI
 * роняет следующий вызов. Методы с error бросают Exception, Go видит его текст.
 */
final class NzPlatform implements PlatformInterface {
    private final NzVpnService service;
    private final NzNetworkMonitor monitor;
    private final NzLocalDns localDns;
    private final Object tunSync = new Object();
    private ParcelFileDescriptor tun;
    private volatile String tunName;

    NzPlatform(NzVpnService service, NzNetworkMonitor monitor) {
        this.service = service;
        this.monitor = monitor;
        this.localDns = new NzLocalDns(monitor);
    }

    // ---- TUN ----

    @Override
    public int openTun(TunOptions options) throws Exception {
        if (VpnService.prepare(service) != null) {
            throw new Exception("android: нет разрешения на VPN (или его забрал другой VPN)");
        }
        String own = service.getPackageName();
        VpnService.Builder builder = service.newBuilder();
        builder.setSession(NzBox.sessionTitle());
        int mtu = options.getMTU();
        if (mtu > 0) {
            builder.setMtu(mtu);
        }
        if (Build.VERSION.SDK_INT >= 29) {
            // Иначе Android считает весь трафик через VPN платным и приложения экономят его зря.
            builder.setMetered(false);
        }

        boolean has4 = false;
        boolean has6 = false;
        RoutePrefixIterator a4 = options.getInet4Address();
        while (a4 != null && a4.hasNext()) {
            RoutePrefix p = a4.next();
            builder.addAddress(p.address(), p.prefix());
            has4 = true;
        }
        RoutePrefixIterator a6 = options.getInet6Address();
        while (a6 != null && a6.hasNext()) {
            RoutePrefix p = a6.next();
            builder.addAddress(p.address(), p.prefix());
            has6 = true;
        }

        if (options.getAutoRoute()) {
            String dnsMode = "";
            try {
                dnsMode = options.getDNSMode().getValue();
            } catch (Throwable ignored) {
                // пусто — значит не выключен
            }
            if (!Libbox.DNSModeDisabled.equals(dnsMode)) {
                for (String server : NzStrings.read(options.getDNSServerAddress())) {
                    builder.addDnsServer(server);
                }
            }
            if (Build.VERSION.SDK_INT >= 33) {
                // С Android 13 есть excludeRoute: маршруты как в конфиге, исключения — системе.
                boolean any4 = addRoutes33(builder, options.getInet4RouteAddress());
                if (!any4 && has4) {
                    builder.addRoute("0.0.0.0", 0);
                }
                boolean any6 = addRoutes33(builder, options.getInet6RouteAddress());
                if (!any6 && has6) {
                    builder.addRoute("::", 0);
                }
                excludeRoutes33(builder, options.getInet4RouteExcludeAddress());
                excludeRoutes33(builder, options.getInet6RouteExcludeAddress());
            } else {
                // Раньше исключать нечем — Go уже вычел исключения из диапазонов (BuildAutoRouteRanges).
                addRanges(builder, options.getInet4RouteRange());
                addRanges(builder, options.getInet6RouteRange());
            }
        }

        applyPackages(builder, options, own);

        if (Build.VERSION.SDK_INT >= 29 && options.isHTTPProxyEnabled()) {
            builder.setHttpProxy(ProxyInfo.buildDirectProxy(options.getHTTPProxyServer(),
                    options.getHTTPProxyServerPort(), NzStrings.read(options.getHTTPProxyBypassDomain())));
        }

        ParcelFileDescriptor pfd = builder.establish();
        if (pfd == null) {
            throw new Exception("android: VPN не поднят — приложение не подготовлено или разрешение отозвано");
        }
        // Go держит свою копию дескриптора (dup), поэтому прежний закрываем только после нового.
        ParcelFileDescriptor old;
        synchronized (tunSync) {
            old = tun;
            tun = pfd;
        }
        closeQuietly(old);
        return pfd.getFd();
    }

    /**
     * Своё приложение — всегда мимо TUN (DESIGN.md): через него ходят ByeDPI
     * и пробы «напрямую». Списки «разрешённых» и «запрещённых» у VpnService
     * взаимоисключающие: при списке разрешённых наш пакет просто в него не
     * входит, при списке запрещённых — дописывается к нему.
     */
    private void applyPackages(VpnService.Builder builder, TunOptions options, String own) {
        List<String> include = NzStrings.read(options.getIncludePackage());
        List<String> exclude = NzStrings.read(options.getExcludePackage());
        if (!include.isEmpty()) {
            if (!exclude.isEmpty()) {
                NzBox.LOG.add(NzLog.NZ, "в конфиге и include_package, и exclude_package — исключения пропущены");
            }
            int added = 0;
            for (String pkg : new LinkedHashSet<>(include)) {
                if (pkg.equals(own)) {
                    continue;
                }
                try {
                    builder.addAllowedApplication(pkg);
                    added++;
                } catch (PackageManager.NameNotFoundException e) {
                    NzBox.LOG.add(NzLog.NZ, "пакет не установлен: " + pkg);
                }
            }
            if (added == 0) {
                // Пустой список разрешённых значит «все» — тогда хотя бы себя вывести.
                tryDisallow(builder, own);
            }
            return;
        }
        Set<String> all = new LinkedHashSet<>(exclude);
        all.add(own);
        for (String pkg : all) {
            if (pkg.equals(own)) {
                tryDisallow(builder, own);
                continue;
            }
            try {
                builder.addDisallowedApplication(pkg);
            } catch (PackageManager.NameNotFoundException e) {
                NzBox.LOG.add(NzLog.NZ, "пакет не установлен: " + pkg);
            }
        }
    }

    private static void tryDisallow(VpnService.Builder builder, String pkg) {
        try {
            builder.addDisallowedApplication(pkg);
        } catch (PackageManager.NameNotFoundException ignored) {
            // своё имя есть всегда
        }
    }

    @TargetApi(33)
    private static boolean addRoutes33(VpnService.Builder builder, RoutePrefixIterator it) throws IOException {
        boolean any = false;
        while (it != null && it.hasNext()) {
            RoutePrefix p = it.next();
            builder.addRoute(new IpPrefix(InetAddress.getByName(p.address()), p.prefix()));
            any = true;
        }
        return any;
    }

    @TargetApi(33)
    private static void excludeRoutes33(VpnService.Builder builder, RoutePrefixIterator it) throws IOException {
        while (it != null && it.hasNext()) {
            RoutePrefix p = it.next();
            builder.excludeRoute(new IpPrefix(InetAddress.getByName(p.address()), p.prefix()));
        }
    }

    private static void addRanges(VpnService.Builder builder, RoutePrefixIterator it) {
        while (it != null && it.hasNext()) {
            RoutePrefix p = it.next();
            builder.addRoute(p.address(), p.prefix());
        }
    }

    /** Закрыть наш дескриптор TUN; VPN исчезнет, когда закроет свой и Go. */
    void closeTun() {
        ParcelFileDescriptor old;
        synchronized (tunSync) {
            old = tun;
            tun = null;
        }
        closeQuietly(old);
    }

    private static void closeQuietly(ParcelFileDescriptor pfd) {
        if (pfd != null) {
            try {
                pfd.close();
            } catch (IOException ignored) {
            }
        }
    }

    @Override
    public void registerMyInterface(String name) {
        tunName = name;
    }

    String tunName() {
        return tunName;
    }

    // ---- свои сокеты мимо туннеля ----

    @Override
    public boolean usePlatformAutoDetectInterfaceControl() {
        // Без этого сокеты самого sing-box уходили бы в собственный TUN по кругу.
        return true;
    }

    @Override
    public void autoDetectInterfaceControl(int fd) throws Exception {
        // Горячий путь: каждый сокет движка. Ничего лишнего.
        if (!service.protect(fd)) {
            throw new Exception("protect(" + fd + ") не удался");
        }
    }

    /**
     * Только в форке (bind_interface у исходящего). Наш конфиг на телефоне
     * bind_interface не пишет (config.md: DNS роутера — потом), но метод
     * обязан работать: сокет защищаем и привязываем к сети с этим именем.
     */
    @Override
    public void bindInterfaceControl(int fd, String interfaceName) throws Exception {
        if (!service.protect(fd)) {
            throw new Exception("protect(" + fd + ") не удался");
        }
        if (interfaceName == null || interfaceName.isEmpty()) {
            return;
        }
        ConnectivityManager cm = monitor.connectivity();
        for (Network n : cm.getAllNetworks()) {
            LinkProperties lp = cm.getLinkProperties(n);
            if (lp != null && interfaceName.equals(lp.getInterfaceName())) {
                // fromFd делает dup: привязка действует на тот же сокет, а копию закрываем.
                ParcelFileDescriptor dup = ParcelFileDescriptor.fromFd(fd);
                try {
                    n.bindSocket(dup.getFileDescriptor());
                } finally {
                    dup.close();
                }
                return;
            }
        }
        throw new Exception("нет сети с интерфейсом " + interfaceName);
    }

    // ---- сеть по умолчанию и интерфейсы ----

    @Override
    public void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) throws Exception {
        monitor.setListener(listener);
    }

    @Override
    public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) throws Exception {
        monitor.setListener(null);
    }

    /**
     * Все сети с интерфейсом (libbox.md §3.3). Сеть по умолчанию обязана здесь
     * быть: Go ищет её по номеру, иначе обновление пропадает. Адреса — без
     * «%зоны» и с длиной префикса в пределах: кривую строку Go не разберёт
     * (MustParsePrefix — паника).
     */
    @Override
    public NetworkInterfaceIterator getInterfaces() throws Exception {
        ConnectivityManager cm = monitor.connectivity();
        ArrayList<NetworkInterface> list = new ArrayList<>();
        HashSet<String> seen = new HashSet<>();
        for (Network n : cm.getAllNetworks()) {
            LinkProperties lp = cm.getLinkProperties(n);
            NetworkCapabilities caps = cm.getNetworkCapabilities(n);
            if (lp == null || caps == null) {
                continue;
            }
            String name = lp.getInterfaceName();
            if (name == null || !seen.add(name)) {
                continue;
            }
            java.net.NetworkInterface ji;
            try {
                ji = java.net.NetworkInterface.getByName(name);
            } catch (Throwable e) {
                ji = null;
            }
            if (ji == null) {
                continue;
            }
            NetworkInterface ni = new NetworkInterface();
            ni.setName(name);
            ni.setIndex(ji.getIndex());
            try {
                ni.setMTU(ji.getMTU());
            } catch (Throwable ignored) {
                // MTU не прочитать — Go обойдётся без него
            }

            ArrayList<String> addresses = new ArrayList<>();
            for (InterfaceAddress ia : ji.getInterfaceAddresses()) {
                InetAddress a = ia.getAddress();
                if (a == null) {
                    continue;
                }
                int prefix = ia.getNetworkPrefixLength();
                int max = a.getAddress().length == 4 ? 32 : 128;
                if (prefix < 0 || prefix > max) {
                    continue;
                }
                addresses.add(plain(a) + "/" + prefix);
            }
            ni.setAddresses(new NzStrings(addresses));

            ArrayList<String> dns = new ArrayList<>();
            for (InetAddress a : lp.getDnsServers()) {
                dns.add(plain(a));
            }
            ni.setDNSServer(new NzStrings(dns));

            ArrayList<String> gateways = new ArrayList<>();
            for (RouteInfo r : lp.getRoutes()) {
                IpPrefix dest = r.getDestination();
                InetAddress gw = r.getGateway();
                if (dest != null && dest.getPrefixLength() == 0 && gw != null && !gw.isAnyLocalAddress()) {
                    String g = plain(gw);
                    if (!gateways.contains(g)) {
                        gateways.add(g);
                    }
                }
            }
            ni.setGateway(new NzStrings(gateways));

            int type;
            if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
                type = Libbox.InterfaceTypeWIFI;
            } else if (caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) {
                type = Libbox.InterfaceTypeCellular;
            } else if (caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) {
                type = Libbox.InterfaceTypeEthernet;
            } else {
                type = Libbox.InterfaceTypeOther;
            }
            ni.setType(type);

            int flags = 0;
            if (caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                flags |= OsConstants.IFF_UP | OsConstants.IFF_RUNNING;
            }
            try {
                if (ji.isLoopback()) {
                    flags |= OsConstants.IFF_LOOPBACK;
                }
                if (ji.isPointToPoint()) {
                    flags |= OsConstants.IFF_POINTOPOINT;
                }
                if (ji.supportsMulticast()) {
                    flags |= OsConstants.IFF_MULTICAST;
                }
            } catch (Throwable ignored) {
                // флаги не прочитать — оставим UP/RUNNING
            }
            ni.setFlags(flags);
            ni.setMetered(!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED));
            list.add(ni);
        }
        return new NzInterfaces(list);
    }

    private static String plain(InetAddress a) {
        try {
            return InetAddress.getByAddress(a.getAddress()).getHostAddress();
        } catch (Throwable e) {
            String s = a.getHostAddress();
            int zone = s.indexOf('%');
            return zone > 0 ? s.substring(0, zone) : s;
        }
    }

    @Override
    public LocalDNSTransport localDNSTransport() {
        return localDns;
    }

    @Override
    public boolean useProcFS() {
        // До Android 10 владельца соединения читают из /proc/net, после — только через ConnectivityManager.
        return Build.VERSION.SDK_INT < 29;
    }

    /** Владелец соединения для правил по приложению (API 29+; ниже Go читает /proc сам). */
    @Override
    public ConnectionOwner findConnectionOwner(int ipProtocol, String sourceAddress, int sourcePort,
                                               String destinationAddress, int destinationPort) throws Exception {
        if (Build.VERSION.SDK_INT < 29) {
            throw new Exception("владелец соединения доступен с Android 10");
        }
        ConnectivityManager cm = monitor.connectivity();
        int uid = cm.getConnectionOwnerUid(ipProtocol,
                new InetSocketAddress(InetAddress.getByName(sourceAddress), sourcePort),
                new InetSocketAddress(InetAddress.getByName(destinationAddress), destinationPort));
        if (uid == android.os.Process.INVALID_UID) {
            throw new Exception("владелец соединения не найден");
        }
        String[] packages = service.getPackageManager().getPackagesForUid(uid);
        ConnectionOwner owner = new ConnectionOwner();
        owner.setUserId(uid);
        ArrayList<String> names = new ArrayList<>();
        if (packages != null) {
            for (String p : packages) {
                names.add(p);
            }
        }
        owner.setUserName(names.isEmpty() ? "" : names.get(0));
        owner.setAndroidPackageNames(new NzStrings(names));
        return owner;
    }

    /** Пакет по uid — для правил по приложению и журнала; null — не найден. */
    String packageNameByUid(int uid) {
        try {
            String[] packages = service.getPackageManager().getPackagesForUid(uid);
            return packages != null && packages.length > 0 ? packages[0] : null;
        } catch (Throwable e) {
            return null;
        }
    }

    /** uid пакета; -1 — не установлен. */
    int uidByPackageName(String packageName) {
        try {
            return service.getPackageManager().getApplicationInfo(packageName, 0).uid;
        } catch (Throwable e) {
            return -1;
        }
    }

    // ---- то, чего у телефона нет или нам не нужно ----

    @Override
    public boolean underNetworkExtension() {
        return false;
    }

    @Override
    public boolean includeAllNetworks() {
        return false;
    }

    /**
     * Правил по сети Wi-Fi наш конфиг не пишет, а имя сети требует права
     * на местоположение — его не просим. null Go понимает как «неизвестно».
     */
    @Override
    public WIFIState readWIFIState() {
        return null;
    }

    @Override
    public void clearDNSCache() {
    }

    /** Уведомления самого движка (например, о сертификате): свой канал на каждый вид. */
    @Override
    public void sendNotification(io.nekohasekai.libbox.Notification notification) throws Exception {
        NotificationManager nm = (NotificationManager) service.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) {
            return;
        }
        String channelId = "nz-core-" + notification.getTypeID();
        if (Build.VERSION.SDK_INT >= 26) {
            String typeName = notification.getTypeName();
            NotificationChannel channel = new NotificationChannel(channelId,
                    typeName == null || typeName.isEmpty() ? "Сообщения движка" : typeName,
                    NotificationManager.IMPORTANCE_DEFAULT);
            nm.createNotificationChannel(channel);
        }
        android.app.Notification.Builder b = Build.VERSION.SDK_INT >= 26
                ? new android.app.Notification.Builder(service, channelId)
                : new android.app.Notification.Builder(service);
        b.setSmallIcon(NzVpnService.smallIcon(service))
                .setContentTitle(NzSecrets.scrub(notification.getTitle()))
                .setAutoCancel(true);
        String subtitle = notification.getSubtitle();
        if (subtitle != null && !subtitle.isEmpty()) {
            b.setSubText(NzSecrets.scrub(subtitle));
        }
        String body = notification.getBody();
        if (body != null && !body.isEmpty()) {
            String clean = NzSecrets.scrub(body);
            b.setContentText(clean).setStyle(new android.app.Notification.BigTextStyle().bigText(clean));
        }
        String url = notification.getOpenURL();
        if (url != null && !url.isEmpty()) {
            Intent open = new Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            b.setContentIntent(PendingIntent.getActivity(service, notification.getTypeID(), open,
                    PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT));
        }
        nm.notify(notification.getIdentifier(), notification.getTypeID(), b.build());
    }

    @Override
    public void cancelNotification(String identifier, int typeID) throws Exception {
        NotificationManager nm = (NotificationManager) service.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) {
            nm.cancel(identifier, typeID);
        }
    }

    @Override
    public void startNeighborMonitor(NeighborUpdateListener listener) throws Exception {
        // Таблица соседей без root недоступна; функциям, которым она нужна, не пользуемся.
    }

    @Override
    public void closeNeighborMonitor(NeighborUpdateListener listener) throws Exception {
    }

    @Override
    public boolean usePlatformShell() {
        return false;
    }

    @Override
    public void checkPlatformShell() throws Exception {
        throw new Exception("оболочка не поддерживается");
    }

    @Override
    public ShellSession openShellSession(PlatformUser user, String command, StringIterator environ, String term,
                                         int rows, int cols) throws Exception {
        throw new Exception("оболочка не поддерживается");
    }

    @Override
    public PlatformUser lookupUser(String username) throws Exception {
        throw new Exception("пользователи не поддерживаются");
    }

    @Override
    public String lookupSFTPServer() throws Exception {
        throw new Exception("SFTP не поддерживается");
    }

    @Override
    public String readSystemSSHHostKey() throws Exception {
        throw new Exception("SSH не поддерживается");
    }

    @Override
    public String tailscaleHostname() {
        String model = Build.MODEL;
        return model == null ? "android" : model;
    }

    @Override
    public boolean usePlatformBridge() {
        return false;
    }

    @Override
    public BridgeSession createBridge(BridgeOptions options) throws Exception {
        throw new Exception("мост не поддерживается");
    }
}
