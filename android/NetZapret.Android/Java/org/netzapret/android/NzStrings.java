package org.netzapret.android;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import io.nekohasekai.libbox.StringIterator;

/**
 * Список строк для Go (адреса интерфейса, DNS, имена пакетов). Методы
 * StringIterator в Go без error: исключение отсюда оставило бы в JNI
 * висящее исключение и сломало бы следующий вызов (libbox.md §1) — поэтому
 * next() за концом отдаёт пустую строку, как iterator.Next в самом libbox.
 */
final class NzStrings implements StringIterator {
    private final List<String> values;
    private int position;

    NzStrings(List<String> values) {
        this.values = values == null ? Collections.<String>emptyList() : values;
    }

    static NzStrings of(String... values) {
        ArrayList<String> list = new ArrayList<>(values.length);
        Collections.addAll(list, values);
        return new NzStrings(list);
    }

    /** Прочитать итератор Go в список; null — пустой список. */
    static List<String> read(StringIterator iterator) {
        ArrayList<String> list = new ArrayList<>();
        if (iterator == null) {
            return list;
        }
        while (iterator.hasNext()) {
            String s = iterator.next();
            if (s != null && !s.isEmpty()) {
                list.add(s);
            }
        }
        return list;
    }

    @Override
    public boolean hasNext() {
        return position < values.size();
    }

    @Override
    public int len() {
        return values.size();
    }

    @Override
    public String next() {
        if (position >= values.size()) {
            return "";
        }
        return values.get(position++);
    }
}
