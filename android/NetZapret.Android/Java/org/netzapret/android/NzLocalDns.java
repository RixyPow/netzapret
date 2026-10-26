package org.netzapret.android;

import android.annotation.TargetApi;
import android.net.DnsResolver;
import android.net.Network;
import android.os.Build;
import android.os.CancellationSignal;
import android.system.ErrnoException;

import java.net.Inet4Address;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;

import io.nekohasekai.libbox.ExchangeContext;
import io.nekohasekai.libbox.Func;
import io.nekohasekai.libbox.LocalDNSTransport;

/**
 * DNS-сервер типа «local» в конфиге (bootstrap: имена панелей подписок
 * и серверов при DNS «авто» и «через туннель», config.md §2.5). Без него
 * Go читал бы /etc/resolv.conf, которого на Android нет. Спрашиваем
 * резолвер настоящей сети, а не VPN: для нашего uid activeNetwork — сам VPN.
 *
 * Go ждёт, пока будет вызван ровно один из success / rawSuccess /
 * errorCode / errnoCode (libbox.md §3.4), поэтому каждая ветка вызывает
 * один из них, а ожидание ограничено сроком — отменённый DnsResolver
 * обратный вызов может и не сделать.
 */
final class NzLocalDns implements LocalDNSTransport {
    private static final int RCODE_SERVFAIL = 2;
    private static final int RCODE_NXDOMAIN = 3;
    private static final long WAIT_SECONDS = 15;

    private static final Executor DIRECT = new Executor() {
        @Override
        public void execute(Runnable command) {
            command.run();
        }
    };

    private final NzNetworkMonitor monitor;

    NzLocalDns(NzNetworkMonitor monitor) {
        this.monitor = monitor;
    }

    @Override
    public boolean raw() {
        return Build.VERSION.SDK_INT >= 29;
    }

    @Override
    @TargetApi(29)
    public void exchange(final ExchangeContext ctx, byte[] message) throws Exception {
        if (Build.VERSION.SDK_INT < 29) {
            throw new Exception("сырой DNS доступен с Android 10");
        }
        Network network = monitor.network();
        if (network == null) {
            throw new Exception("missing default interface");
        }
        final CountDownLatch done = new CountDownLatch(1);
        final CancellationSignal cancel = new CancellationSignal();
        ctx.onCancel(new Func() {
            @Override
            public void invoke() {
                try {
                    cancel.cancel();
                } catch (Throwable ignored) {
                }
                done.countDown();
            }
        });
        DnsResolver.getInstance().rawQuery(network, message, DnsResolver.FLAG_NO_RETRY, DIRECT, cancel,
                new DnsResolver.Callback<byte[]>() {
                    @Override
                    public void onAnswer(byte[] answer, int rcode) {
                        try {
                            if (rcode == 0) {
                                ctx.rawSuccess(answer);
                            } else {
                                ctx.errorCode(rcode);
                            }
                        } finally {
                            done.countDown();
                        }
                    }

                    @Override
                    public void onError(DnsResolver.DnsException error) {
                        try {
                            Throwable cause = error.getCause();
                            if (cause instanceof ErrnoException) {
                                ctx.errnoCode(((ErrnoException) cause).errno);
                            } else {
                                ctx.errorCode(RCODE_SERVFAIL);
                            }
                        } finally {
                            done.countDown();
                        }
                    }
                });
        if (!done.await(WAIT_SECONDS, TimeUnit.SECONDS)) {
            cancel.cancel();
            throw new Exception("локальный DNS не ответил за " + WAIT_SECONDS + " с");
        }
    }

    @Override
    public void lookup(ExchangeContext ctx, String network, String domain) throws Exception {
        Network underlying = monitor.network();
        if (underlying == null) {
            throw new Exception("missing default interface");
        }
        String name = domain.endsWith(".") ? domain.substring(0, domain.length() - 1) : domain;
        InetAddress[] all;
        try {
            all = underlying.getAllByName(name);
        } catch (UnknownHostException e) {
            ctx.errorCode(RCODE_NXDOMAIN);
            return;
        }
        boolean want4 = !"ip6".equals(network);
        boolean want6 = !"ip4".equals(network);
        StringBuilder sb = new StringBuilder();
        for (InetAddress a : all) {
            if ((a instanceof Inet4Address && want4) || (a instanceof Inet6Address && want6)) {
                if (sb.length() > 0) {
                    sb.append('\n');
                }
                // Без «%зоны»: Go разбирает адрес строго.
                sb.append(InetAddress.getByAddress(a.getAddress()).getHostAddress());
            }
        }
        ctx.success(sb.toString());
    }
}
