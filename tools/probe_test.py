#!/usr/bin/env python3
"""
probe_test.py — тест-система стабильности выдачи источников lampac.

Проверяет /lite/events на:
  - race при параллельных запросах (частичный links кешируется на лету);
  - нестабильность холодного кеша (неполный результат фиксируется на 5 мин);
  - флапающие балансеры (то есть в выдаче, то нет);
  - консистентность кеша (warm-ответ == cold-ответу).

Запуск (примеры):
  python3 tools/probe_test.py stability  --movie-id 950396
  python3 tools/probe_test.py catalog    --movies 25
  python3 tools/probe_test.py cold       --movie-id 950396 --runs 6 --reset-cmd "bash dev-docker.sh reup"
  python3 tools/probe_test.py concurrent --movie-id 950396 --concurrency 20 --reset-cmd "bash dev-docker.sh reup"
  python3 tools/probe_test.py all        --movies 15 --reset-cmd "bash dev-docker.sh reup"

Exit code: 0 — аномалий не найдено, 1 — найдены, 2 — ошибка окружения.
"""

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor

DEFAULT_BASE = "http://localhost:9118"
CATALOG_URL = "https://tmdb.cub.rip/top/fire/movie"

C_RED = "\033[0;31m"
C_GRN = "\033[0;32m"
C_YEL = "\033[1;33m"
C_DIM = "\033[2m"
C_BLD = "\033[1m"
C_OFF = "\033[0m"


def c(color, s):
    return f"{color}{s}{C_OFF}"


# ──────────────────────────── HTTP ────────────────────────────

def http_get(url, timeout=60):
    """Возвращает (status, body_text). При сетевой ошибке — (0, '<error>')."""
    req = urllib.request.Request(url, headers={"User-Agent": "lampac-probe-test/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:
        return 0, f"<{type(e).__name__}: {e}>"


# ──────────────────────────── модель ────────────────────────────

class Movie:
    __slots__ = ("id", "title", "original_title", "original_language", "year",
                 "imdb_id", "kinopoisk_id")

    def __init__(self, id, title="", original_title="", original_language="",
                 year=0, imdb_id="", kinopoisk_id=0):
        self.id = str(id)
        self.title = title or ""
        self.original_title = original_title or ""
        self.original_language = original_language or ""
        self.year = year or 0
        self.imdb_id = imdb_id or ""
        self.kinopoisk_id = kinopoisk_id or 0

    def label(self):
        t = self.title or self.original_title or self.id
        return f"{t} ({self.id})"


def get_catalog(limit, email, timeout=30):
    """Тянет список фильмов из tmdb-каталога. Возвращает list[Movie]."""
    movies = []
    page = 1
    while len(movies) < limit and page <= 10:
        url = f"{CATALOG_URL}?page={page}&email={urllib.parse.quote(email)}"
        status, body = http_get(url, timeout)
        if status != 200:
            raise RuntimeError(f"каталог вернул HTTP {status}: {body[:200]}")
        try:
            data = json.loads(body)
        except json.JSONDecodeError as e:
            raise RuntimeError(f"каталог отдал не-JSON: {e}")
        results = data.get("results") or []
        if not results:
            break
        for r in results:
            rd = r.get("release_date") or r.get("first_air_date") or ""
            year = int(rd[:4]) if rd[:4].isdigit() else 0
            movies.append(Movie(
                id=r.get("id"),
                title=r.get("title") or r.get("name") or "",
                original_title=r.get("original_title") or r.get("original_name") or "",
                original_language=r.get("original_language") or "",
                year=year,
            ))
            if len(movies) >= limit:
                break
        page += 1
    return movies


# ──────────────────────────── events ────────────────────────────

class EventsResult:
    __slots__ = ("ok", "balansers", "count", "elapsed", "note", "http_status")

    def __init__(self, ok, balansers, elapsed, note="", http_status=0):
        self.ok = ok
        self.balansers = balansers          # list[str] — плагины-источники
        self.count = len(balansers)
        self.elapsed = elapsed
        self.note = note
        self.http_status = http_status


def events_url(base, m, account_email, life=False):
    q = {
        "id": m.id,
        "imdb_id": m.imdb_id,
        "kinopoisk_id": m.kinopoisk_id,
        "title": m.title,
        "original_title": m.original_title,
        "serial": 0,
        "original_language": m.original_language,
        "year": m.year,
        "source": "cub",
        "clarification": 0,
        "similar": "false",
        "rchtype": "web",
        "account_email": account_email,
    }
    if life:
        q["life"] = "true"
    return f"{base.rstrip('/')}/lite/events?" + urllib.parse.urlencode(q)


def parse_events_body(body):
    """Разбирает тело ответа /lite/events.
    Возвращает (kind, payload):
      kind='sources' → payload=list[str] балансеров
      kind='life'    → payload=memkey (нужен поллинг)
      kind='error'   → payload=текст
      kind='empty'   → payload=None
    """
    body = body.strip()
    if not body:
        return "empty", None
    try:
        data = json.loads(body)
    except json.JSONDecodeError:
        return "error", f"не-JSON ответ: {body[:160]}"

    if isinstance(data, list):
        balansers = []
        for e in data:
            if not isinstance(e, dict):
                continue
            b = e.get("balanser") or e.get("plugin")
            if b:
                balansers.append(str(b))
        return "sources", balansers

    if isinstance(data, dict):
        if data.get("life") is True:
            return "life", data.get("memkey")
        if data.get("accsdb") or data.get("msg"):
            return "error", str(data.get("msg") or "accsdb")
        return "empty", None

    return "error", "неизвестная форма ответа"


def fetch_events(base, m, account_email, timeout=60, poll_max_s=25):
    """Один полный запрос /lite/events с обработкой life-поллинга."""
    t0 = time.monotonic()
    status, body = http_get(events_url(base, m, account_email), timeout)
    if status == 0:
        return EventsResult(False, [], time.monotonic() - t0, body, status)
    if status != 200:
        return EventsResult(False, [], time.monotonic() - t0, f"HTTP {status}", status)

    kind, payload = parse_events_body(body)

    # life-режим: поллим тот же URL пока не придёт финал.
    deadline = time.monotonic() + poll_max_s
    while kind == "life" and time.monotonic() < deadline:
        time.sleep(0.5)
        status, body = http_get(events_url(base, m, account_email), timeout)
        if status != 200:
            return EventsResult(False, [], time.monotonic() - t0, f"poll HTTP {status}", status)
        kind, payload = parse_events_body(body)

    elapsed = time.monotonic() - t0
    if kind == "sources":
        return EventsResult(True, payload, elapsed, "", status)
    if kind == "empty":
        return EventsResult(True, [], elapsed, "пустой ответ", status)
    if kind == "life":
        return EventsResult(False, [], elapsed, "life-поллинг не завершился", status)
    return EventsResult(False, [], elapsed, str(payload), status)


# ──────────────────────────── сброс кеша ────────────────────────────

def wait_healthy(base, timeout_s=90):
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        status, _ = http_get(base.rstrip("/") + "/", timeout=5)
        if status not in (0,):
            return True
        time.sleep(1.5)
    return False


def reset_cache(reset_cmd, base):
    """Выполняет reset_cmd (рестарт инстанса) и ждёт готовности."""
    if not reset_cmd:
        return False
    print(c(C_DIM, f"  ↻ сброс кеша: {reset_cmd}"))
    try:
        subprocess.run(reset_cmd, shell=True, check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except subprocess.CalledProcessError as e:
        print(c(C_RED, f"  reset-cmd упал: {e}"))
        return False
    if not wait_healthy(base):
        print(c(C_RED, "  инстанс не поднялся после сброса"))
        return False
    time.sleep(2)
    return True


# ──────────────────────────── режимы ────────────────────────────

def pick_movie(args):
    """Возвращает один Movie по --movie-id, либо первый из каталога."""
    if args.movie_id:
        return Movie(id=args.movie_id, title=f"movie#{args.movie_id}", original_language="en")
    movies = get_catalog(1, args.email)
    if not movies:
        raise RuntimeError("каталог пуст")
    return movies[0]


def mode_stability(args):
    """N запросов одного фильма подряд (тёплый кеш). count не должен меняться."""
    m = pick_movie(args)
    print(c(C_BLD, f"\n[stability] {m.label()} — {args.runs} прогонов, тёплый кеш"))
    counts, results = [], []
    for i in range(args.runs):
        r = fetch_events(args.base, m, args.email)
        results.append(r)
        counts.append(r.count if r.ok else -1)
        tag = c(C_GRN, "ok") if r.ok else c(C_RED, "FAIL")
        print(f"  #{i+1:2}: count={r.count:2}  {r.elapsed:5.1f}s  {tag}  {r.note}")
        time.sleep(0.4)

    ok_counts = [x for x in counts if x >= 0]
    distinct = sorted(set(ok_counts))
    anomaly = len(distinct) > 1 or any(x < 0 for x in counts)
    if anomaly:
        print(c(C_RED, f"  ✗ АНОМАЛИЯ: count нестабилен при тёплом кеше → {distinct} "
                       f"(race в выдаче или порча кеша)"))
    else:
        print(c(C_GRN, f"  ✓ стабильно: count={distinct[0] if distinct else 0}"))
    return not anomaly


def mode_cold(args):
    """M прогонов, каждый — холодный кеш. Холодный count должен быть стабилен."""
    m = pick_movie(args)
    print(c(C_BLD, f"\n[cold] {m.label()} — {args.runs} холодных прогонов"))
    if not args.reset_cmd:
        print(c(C_YEL, "  ⚠ --reset-cmd не задан: без сброса кеш будет тёплым, тест бессмысленен"))
    counts = []
    for i in range(args.runs):
        if args.reset_cmd and not reset_cache(args.reset_cmd, args.base):
            return False
        r = fetch_events(args.base, m, args.email)
        counts.append(r.count if r.ok else -1)
        bal = ",".join(sorted(r.balansers))
        tag = c(C_GRN, "ok") if r.ok else c(C_RED, "FAIL")
        print(f"  cold #{i+1}: count={r.count:2}  {r.elapsed:5.1f}s  {tag}  [{bal}]  {r.note}")

    distinct = sorted(set(counts))
    anomaly = len(distinct) > 1
    if anomaly:
        print(c(C_RED, f"  ✗ АНОМАЛИЯ: холодный count скачет → {distinct} "
                       f"(частичный результат фиксируется в кеше)"))
    else:
        print(c(C_GRN, f"  ✓ холодный кеш стабилен: count={distinct[0]}"))
    return not anomaly


def mode_concurrent(args):
    """K одновременных запросов на холодный кеш. Все должны вернуть одно и то же."""
    m = pick_movie(args)
    k = args.concurrency
    print(c(C_BLD, f"\n[concurrent] {m.label()} — {k} параллельных запросов, холодный кеш"))
    if args.reset_cmd and not reset_cache(args.reset_cmd, args.base):
        return False
    if not args.reset_cmd:
        print(c(C_YEL, "  ⚠ --reset-cmd не задан: кеш тёплый, race не воспроизведётся"))

    with ThreadPoolExecutor(max_workers=k) as ex:
        results = list(ex.map(lambda _: fetch_events(args.base, m, args.email), range(k)))

    counts = Counter(r.count if r.ok else -1 for r in results)
    fails = [r for r in results if not r.ok]
    for cnt, n in sorted(counts.items()):
        label = "FAIL" if cnt < 0 else f"count={cnt}"
        print(f"  {n:3}× {label}")
    if fails:
        print(c(C_DIM, "  причины FAIL:"))
        for note, n in Counter(r.note for r in fails).most_common():
            print(c(C_DIM, f"    {n:3}× {note}"))
    full = max((r.count for r in results if r.ok), default=0)
    partial = sum(1 for r in results if r.ok and r.count < full)
    anomaly = len(counts) > 1 or bool(fails)
    if anomaly:
        print(c(C_RED, f"  ✗ АНОМАЛИЯ: {partial} запросов получили частичный результат, "
                       f"{len(fails)} упали (race на недозаполненном links)"))
    else:
        print(c(C_GRN, f"  ✓ все {k} запросов согласованы: count={full}"))
    return not anomaly


def mode_catalog(args):
    """Проход по каталогу: для каждого фильма cold-запрос + warm-повтор."""
    movies = get_catalog(args.movies, args.email)
    print(c(C_BLD, f"\n[catalog] {len(movies)} фильмов — cold-запрос + warm-повтор"))
    print(c(C_DIM, "  фильм                                  cold  warm  балансеры"))

    suspicious, flap, errors = [], [], []
    balanser_seen = Counter()
    balanser_total = Counter()

    for m in movies:
        cold = fetch_events(args.base, m, args.email)
        warm = fetch_events(args.base, m, args.email)

        for b in set(cold.balansers) | set(warm.balansers):
            balanser_seen[b] += 1
        for b in cold.balansers:
            balanser_total[b] += 1

        if not cold.ok:
            errors.append((m, cold.note))
            mark = c(C_RED, "ERR")
            print(f"  {m.label()[:38]:38}  {mark}        {cold.note}")
            continue

        ref = max(cold.count, warm.count)
        cold_s, warm_s = f"{cold.count:4}", f"{warm.count:4}"
        if warm.count != cold.count:
            warm_s = c(C_YEL, warm_s)
            flap.append((m, cold.count, warm.count))
        if cold.count == 0:
            cold_s = c(C_RED, cold_s)
            suspicious.append((m, cold.count))
        elif cold.count <= 2:
            cold_s = c(C_YEL, cold_s)
            suspicious.append((m, cold.count))

        bal = ",".join(sorted(set(cold.balansers)))
        print(f"  {m.label()[:38]:38}  {cold_s}  {warm_s}  {c(C_DIM, bal[:60])}")
        time.sleep(0.2)

    print()
    print(c(C_BLD, "  Сводка:"))
    print(f"    фильмов проверено:      {len(movies)}")
    print(f"    с 0-2 источниками:      {len(suspicious)}")
    print(f"    cold≠warm (флап кеша):  {len(flap)}")
    print(f"    ошибок запроса:         {len(errors)}")
    if suspicious:
        print(c(C_YEL, "    подозрительно мало источников:"))
        for m, cnt in suspicious:
            print(f"      - {m.label()}: {cnt}")
    if flap:
        print(c(C_RED, "    кеш зафиксировал разный результат cold vs warm:"))
        for m, cd, wm in flap:
            print(f"      - {m.label()}: cold={cd} warm={wm}")

    anomaly = bool(flap) or bool(errors)
    if anomaly:
        print(c(C_RED, "  ✗ АНОМАЛИИ найдены (см. выше)"))
    else:
        print(c(C_GRN, "  ✓ грубых аномалий нет (но проверь список 'мало источников')"))
    return not anomaly


MODES = {
    "stability": mode_stability,
    "cold": mode_cold,
    "concurrent": mode_concurrent,
    "catalog": mode_catalog,
}


def mode_all(args):
    ok = True
    ok &= mode_catalog(args)
    ok &= mode_stability(args)
    if args.reset_cmd:
        ok &= mode_cold(args)
        ok &= mode_concurrent(args)
    else:
        print(c(C_YEL, "\n[all] cold/concurrent пропущены — нужен --reset-cmd"))
    return ok


def main():
    p = argparse.ArgumentParser(description="Тест-система стабильности источников lampac")
    p.add_argument("mode", choices=list(MODES) + ["all"])
    p.add_argument("--base", default=DEFAULT_BASE, help=f"URL инстанса (default {DEFAULT_BASE})")
    p.add_argument("--email", default="findz", help="account_email для запросов")
    p.add_argument("--movie-id", default="", help="tmdb id одного фильма (для stability/cold/concurrent)")
    p.add_argument("--movies", type=int, default=20, help="сколько фильмов взять из каталога")
    p.add_argument("--runs", type=int, default=8, help="прогонов в stability/cold")
    p.add_argument("--concurrency", type=int, default=20, help="параллельных запросов в concurrent")
    p.add_argument("--reset-cmd", default="", help='команда сброса кеша, напр. "bash dev-docker.sh reup"')
    args = p.parse_args()

    status, _ = http_get(args.base.rstrip("/") + "/", timeout=5)
    if status == 0:
        print(c(C_RED, f"инстанс {args.base} недоступен"))
        return 2

    try:
        ok = mode_all(args) if args.mode == "all" else MODES[args.mode](args)
    except RuntimeError as e:
        print(c(C_RED, f"ошибка: {e}"))
        return 2

    print()
    print(c(C_GRN, "ИТОГ: аномалий не найдено") if ok else c(C_RED, "ИТОГ: найдены аномалии"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
