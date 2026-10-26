package org.netzapret.android;

import java.util.List;

import io.nekohasekai.libbox.NetworkInterface;
import io.nekohasekai.libbox.NetworkInterfaceIterator;

/** Ответ getInterfaces(): список интерфейсов для Go. Как и NzStrings — без исключений. */
final class NzInterfaces implements NetworkInterfaceIterator {
    private final List<NetworkInterface> values;
    private int position;

    NzInterfaces(List<NetworkInterface> values) {
        this.values = values;
    }

    @Override
    public boolean hasNext() {
        return position < values.size();
    }

    @Override
    public NetworkInterface next() {
        if (position >= values.size()) {
            return null;
        }
        return values.get(position++);
    }
}
