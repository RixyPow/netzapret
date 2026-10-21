#!/usr/bin/env python3
"""
Генератор браузероподобного (Chrome) TLS ClientHello (.bin) для zapret.

Основан на РЕАЛЬНОМ ClientHello современного Chrome, снятом Wireshark
(JA4 t13d1516h2..., с X25519MLKEM768, ECH, ALPS, compress_certificate).
Шаблон лежит в chrome_template.hex рядом со скриптом.

При каждом запуске из шаблона собирается СВЕЖИЙ ClientHello:
  * random(32), legacy_session_id, значения GREASE  -> новые (os.urandom);
  * публичные ключи в key_share (X25519MLKEM768 + x25519) -> новые случайные
    байты нужной длины (для fake-пакета крипто-валидность не требуется);
  * payload расширения ECH (encrypted_client_hello) -> новые случайные байты;
  * SNI подставляется ваш, все длины/размер пересчитываются корректно.
Порядок cipher suites и расширений НЕ меняется -> JA3/JA4 = как у настоящего Chrome.

Использование:
    python gen_chrome_ch.py <домен> [выход.bin]

Пример:
    python gen_chrome_ch.py mysite.ru
        -> tls_clienthello_mysite_ru.bin
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATE_PATH = os.path.join(HERE, "chrome_template.hex")


def u16(v):
    return bytes([(v >> 8) & 0xFF, v & 0xFF])


def r16(b, i):
    return (b[i] << 8) | b[i + 1]


def is_grease(v):
    return (v >> 8) == (v & 0xFF) and (v & 0x0F) == 0x0A


def new_grease():
    n = os.urandom(1)[0] & 0xF0
    return ((n << 8) | n | 0x0A0A) & 0xFFFF


def parse_extensions(data):
    exts, p = [], 0
    while p + 4 <= len(data):
        etype = r16(data, p)
        elen = r16(data, p + 2)
        body = data[p + 4:p + 4 + elen]
        exts.append([etype, bytearray(body)])
        p += 4 + elen
    return exts


def rebuild_key_share(body):
    """body = client_shares: [group(2) len(2) key(len)]* — освежаем ключи."""
    out, p = bytearray(), 2  # первые 2 байта — длина списка, пересчитаем
    while p + 4 <= len(body):
        group = r16(body, p)
        klen = r16(body, p + 2)
        key = body[p + 4:p + 4 + klen]
        if is_grease(group):
            group = new_grease()                      # GREASE-запись, ключ 1 байт оставляем
            newkey = key
        else:
            newkey = os.urandom(klen)                 # свежий публичный ключ
        out += u16(group) + u16(klen) + newkey
        p += 4 + klen
    return u16(len(out)) + out


def rebuild_supported_groups(body):
    """[list_len(2)] [group(2)]* — освежаем GREASE-элементы."""
    out, p = bytearray(), 2
    while p + 2 <= len(body):
        g = r16(body, p)
        out += u16(new_grease() if is_grease(g) else g)
        p += 2
    return u16(len(out)) + out


def rebuild_supported_versions(body):
    """[list_len(1)] [ver(2)]* — освежаем GREASE."""
    n = body[0]
    out, p = bytearray(), 1
    while p + 2 <= 1 + n:
        v = r16(body, p)
        out += u16(new_grease() if is_grease(v) else v)
        p += 2
    return bytes([len(out)]) + out


def build_sni(host_b):
    entry = b"\x00" + u16(len(host_b)) + host_b        # name_type=host_name + name
    return u16(len(entry)) + entry                     # server_name_list


def build_client_hello(host: str) -> bytes:
    tpl = bytes.fromhex(open(TEMPLATE_PATH).read().strip())
    assert tpl[0] == 0x16 and tpl[5] == 0x01, "шаблон не ClientHello"

    # --- разбор фиксированной части ---
    client_version = tpl[9:11]
    p = 43                                              # 9 + 2(version) + 32(random)
    sid_len = tpl[43]
    p = 44 + sid_len
    cs_len = r16(tpl, p); p += 2
    ciphers = tpl[p:p + cs_len]; p += cs_len
    comp_len = tpl[p]; p += 1
    comp = tpl[p:p + comp_len]; p += comp_len
    ext_len = r16(tpl, p); p += 2
    ext_data = tpl[p:p + ext_len]

    # --- свежие переменные ---
    random32 = os.urandom(32)
    session_id = os.urandom(sid_len) if sid_len else b""

    # cipher suites: освежить GREASE
    cbuf = bytearray()
    for i in range(0, len(ciphers), 2):
        c = r16(ciphers, i)
        cbuf += u16(new_grease() if is_grease(c) else c)
    ciphers = bytes(cbuf)

    # extensions
    exts = parse_extensions(ext_data)
    host_b = host.encode()
    rebuilt = bytearray()
    for etype, body in exts:
        if is_grease(etype):
            etype = new_grease()                        # GREASE-расширение
        elif etype == 0x0000:                           # server_name
            body = bytearray(build_sni(host_b))
        elif etype == 0x000A:                           # supported_groups
            body = bytearray(rebuild_supported_groups(body))
        elif etype == 0x0033:                           # key_share
            body = bytearray(rebuild_key_share(body))
        elif etype == 0x002B:                           # supported_versions
            body = bytearray(rebuild_supported_versions(body))
        elif etype == 0xFE0D:                           # ECH — освежить payload
            body = bytearray(os.urandom(len(body)))
        rebuilt += u16(etype) + u16(len(body)) + body

    extensions = u16(len(rebuilt)) + rebuilt
    hs_body = (client_version + random32
               + bytes([len(session_id)]) + session_id
               + u16(len(ciphers)) + ciphers
               + bytes([len(comp)]) + comp
               + extensions)
    handshake = b"\x01" + bytes([
        (len(hs_body) >> 16) & 0xFF,
        (len(hs_body) >> 8) & 0xFF,
        len(hs_body) & 0xFF,
    ]) + hs_body
    return b"\x16\x03\x01" + u16(len(handshake)) + handshake


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    host = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else \
        f"tls_clienthello_{host.replace('.', '_')}.bin"
    data = build_client_hello(host)
    with open(out, "wb") as f:
        f.write(data)
    print(f"[+] {out}: {len(data)} байт, record={data[:3].hex(' ')}, SNI={host}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
