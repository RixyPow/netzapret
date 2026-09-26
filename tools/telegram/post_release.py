"""Пост о выпуске в Telegram-канал: примечания выпуска в разметке Telegram.

Зовётся из .github/workflows/telegram-release.yml. Берёт текст выпуска —
тот, что release.cmd собрал из docs/release-notes.md и подвала с хэшами, —
и переводит его в HTML, который понимает Bot API: Telegram не читает
Markdown GitHub (## заголовки, **жирный**).

Пост короткий (владелец, 26.09): разделы и заголовки пунктов, без пояснений,
и ссылка на выпуск, где полный текст, хэши и архив. Хвост «Чем это собрано
и как сверить» в пост не идёт вовсе.

Запуск руками, без отправки:
    RELEASE_BODY="$(cat notes.md)" RELEASE_TAG=v0.8.7 python post_release.py --dry-run

Переменные: RELEASE_BODY, RELEASE_TAG, RELEASE_URL, TELEGRAM_BOT_TOKEN,
TELEGRAM_CHAT_ID. Токен никуда не печатается — ни в вывод, ни в ошибку.
"""

import html
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

# Предел Bot API на одно сообщение, в символах.
LIMIT = 4096

FOOTER = "## Чем это собрано"


def inline(text: str) -> str:
    """Экранирует HTML и переводит **жирный** и `код` в теги Telegram."""
    text = html.escape(text, quote=False)
    text = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", text)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)
    return text


def convert(body: str) -> list[str]:
    """
    Примечания выпуска — коротко: разделы и заголовки пунктов, без пояснений.

    Владелец 26.09: пост с полным текстом в канале выглядел громадой. От
    пункта «**Свои DNS.** В разделе «DNS» можно…» остаётся «• Свои DNS»,
    а всё остальное — по ссылке на выпуск. Абзацы без жирного начала —
    пояснения, в пост не идут.
    """
    notes = body.split(FOOTER, 1)[0]
    blocks = []
    items = []

    def flush() -> None:
        if items:
            blocks.append("\n".join(items))
            items.clear()

    for raw in re.split(r"\n\s*\n", notes.replace("\r\n", "\n")):
        block = raw.strip()

        if not block:
            continue

        # «# Что нового» — заголовок самих примечаний; в посте его место
        # занимает название выпуска.
        if block.startswith("# "):
            continue

        if block.startswith("## "):
            flush()
            blocks.append(f"<b>{inline(block[3:].strip().rstrip(':'))}</b>")
            continue

        lead = re.match(r"\*\*(.+?)\*\*", block)

        if lead:
            items.append("• " + inline(lead.group(1).strip().rstrip(".")))

    flush()

    # Раздел и его пункты — одним абзацем: заголовок сразу над списком.
    merged = []

    for block in blocks:
        if merged and merged[-1].startswith("<b>") and "\n" not in merged[-1] and block.startswith("• "):
            merged[-1] += "\n" + block
        else:
            merged.append(block)

    return merged


def compose(body: str, tag: str, url: str) -> str:
    version = tag.lstrip("v")
    head = f"<b>NetZapret {html.escape(version)}</b>"

    # Хэш архива — на странице выпуска, в канале он только добавлял длины.
    tail = [f'<a href="{html.escape(url, quote=True)}">Полный список изменений и скачивание — на GitHub</a>']

    blocks = convert(body)
    ending = "\n\n".join(tail)

    # Не влезает — режем по абзацам, а не посреди тега: разорванный <b>
    # Telegram отвергнет всё сообщение целиком.
    while blocks:
        text = "\n\n".join([head, *blocks, ending])

        if len(text) <= LIMIT:
            return text

        blocks.pop()
        ending = "…полностью — на GitHub.\n\n" + "\n\n".join(tail)

    return "\n\n".join([head, ending])


def send(text: str) -> None:
    token = os.environ["TELEGRAM_BOT_TOKEN"]
    chat = os.environ["TELEGRAM_CHAT_ID"]

    data = urllib.parse.urlencode({
        "chat_id": chat,
        "text": text,
        "parse_mode": "HTML",
        "disable_web_page_preview": "true",
    }).encode()

    request = urllib.request.Request(f"https://api.telegram.org/bot{token}/sendMessage", data=data)

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            answer = json.load(response)
    except urllib.error.HTTPError as error:
        # Описание ошибки от Telegram — без адреса запроса: в нём токен.
        try:
            answer = json.load(error)
        except ValueError:
            answer = {"description": f"HTTP {error.code}"}

    if not answer.get("ok"):
        sys.exit(f"Telegram отказал: {answer.get('description', 'без объяснения')}")

    print("Отправлено в", chat)


def main() -> None:
    body = os.environ.get("RELEASE_BODY", "")
    tag = os.environ.get("RELEASE_TAG", "")
    url = os.environ.get("RELEASE_URL") or f"https://github.com/RixyPow/netzapret/releases/tag/{tag}"

    if not body.strip() or not tag:
        sys.exit("Нет текста выпуска или тега — отправлять нечего.")

    text = compose(body, tag, url)

    if "--dry-run" in sys.argv:
        print(text)
        print(f"\n[{len(text)} символов из {LIMIT}]")
        return

    send(text)


if __name__ == "__main__":
    main()
