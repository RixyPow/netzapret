package org.netzapret.android;

import java.util.Set;
import java.util.concurrent.CopyOnWriteArraySet;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Вычистка секретов из текста, который уходит в журнал, в status() и в ответы
 * команд. Ссылка подписки равносильна паролю, ключи серверов — тоже
 * (CLAUDE.md, «Секреты»); ошибки Go печатают куски конфига, а в конфиге
 * телефона лежат ключи серверов и пароль ByeDPI. Поэтому всё, что Java
 * отдаёт наружу текстом, проходит через {@link #scrub}.
 */
final class NzSecrets {
    private NzSecrets() {
    }

    private static final String HIDDEN = "<скрыто>";

    /** Строки, известные как секрет дословно: пароль ByeDPI этой сессии. */
    private static final Set<String> LITERALS = new CopyOnWriteArraySet<>();

    private static final Pattern UUID = Pattern.compile(
            "\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b");

    // Ссылки серверов несут ключ в себе целиком — прячется всё после схемы.
    private static final Pattern PROXY_URI = Pattern.compile(
            "(?i)\\b(vless|vmess|trojan|ssr?|hysteria2?|hy2|tuic|anytls|wireguard|wg|socks5?|naive\\+https?|mtproto|tg)://\\S+");

    // Имя и пароль в http(s)-ссылке.
    private static final Pattern URL_USERINFO = Pattern.compile("(?i)\\b(https?://)[^/\\s@]+@");

    // Ссылка подписки — обычный https: токен сидит в запросе или длинным куском пути.
    private static final Pattern URL_QUERY = Pattern.compile("(?i)\\b(https?://[^\\s?#\"']+)\\?[^\\s\"']*");
    private static final Pattern URL_TOKEN = Pattern.compile("(?i)(https?://[^\\s\"']*?/)[A-Za-z0-9_\\-=.]{20,}");

    // Поля конфига sing-box с ключами, если ошибка напечатала кусок JSON.
    private static final Pattern JSON_SECRET = Pattern.compile(
            "(?i)(\"(?:password|uuid|private_key|pre_shared_key|psk|auth|auth_str|token|secret|short_id|public_key|key|username)\"\\s*:\\s*\")[^\"]*\"");

    static void register(String literal) {
        if (literal != null && literal.length() >= 4) {
            LITERALS.add(literal);
        }
    }

    static String scrub(String text) {
        if (text == null || text.isEmpty()) {
            return text == null ? "" : text;
        }
        String s = text;
        for (String literal : LITERALS) {
            if (s.contains(literal)) {
                s = s.replace(literal, HIDDEN);
            }
        }
        s = PROXY_URI.matcher(s).replaceAll("$1://" + Matcher.quoteReplacement(HIDDEN));
        s = URL_USERINFO.matcher(s).replaceAll("$1" + Matcher.quoteReplacement(HIDDEN) + "@");
        s = URL_QUERY.matcher(s).replaceAll("$1?" + Matcher.quoteReplacement(HIDDEN));
        s = URL_TOKEN.matcher(s).replaceAll("$1" + Matcher.quoteReplacement(HIDDEN));
        s = JSON_SECRET.matcher(s).replaceAll("$1" + Matcher.quoteReplacement(HIDDEN) + "\"");
        s = UUID.matcher(s).replaceAll("<uuid>");
        return s;
    }

    /** Текст исключения без секретов; пустое сообщение — имя класса. */
    static String describe(Throwable error) {
        if (error == null) {
            return "";
        }
        String message = error.getMessage();
        if (message == null || message.trim().isEmpty()) {
            Throwable cause = error.getCause();
            if (cause != null && cause != error && cause.getMessage() != null) {
                message = cause.getMessage();
            } else {
                message = error.getClass().getSimpleName();
            }
        }
        return scrub(message.trim());
    }
}
