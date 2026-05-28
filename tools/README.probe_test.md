# probe_test.py — тест-система стабильности источников

Проверяет `/lite/events`: ловит race, нестабильный холодный кеш и флапающие балансеры.
Только stdlib Python 3.8+, зависимостей нет.

## Зачем

Симптом: для фильма то 5+ источников, то 1-2, то 0. Причина — в [OnlineApi.cs:765-788](../Online/OnlineApi.cs#L765-L788):
пустой `links` кешируется на 5 минут **до** заполнения `checkSearch`-тасками; параллельный
запрос видит недозаполненный список и отдаёт частичный результат, который фиксируется в кеше.

## Режимы

| Режим | Что делает | Какую аномалию ловит |
|-------|-----------|----------------------|
| `stability`  | N запросов одного фильма, тёплый кеш | count меняется при тёплом кеше → race в выдаче / порча кеша |
| `cold`       | N прогонов, каждый со сбросом кеша | холодный count скачет → частичный результат фиксируется |
| `concurrent` | K одновременных запросов, холодный кеш | запросы получают разный count → race на недозаполненном `links` |
| `catalog`    | проход по каталогу: cold-запрос + warm-повтор | cold≠warm, фильмы с 0-2 источниками |
| `all`        | catalog + stability (+ cold/concurrent если задан `--reset-cmd`) | всё сразу |

## Запуск

```bash
# быстрая проверка по каталогу — сброс кеша не нужен
python3 tools/probe_test.py catalog --movies 25

# тёплый кеш одного фильма
python3 tools/probe_test.py stability --movie-id 950396

# холодный кеш — нужен способ сброса (рестарт инстанса)
python3 tools/probe_test.py cold       --movie-id 950396 --runs 6  --reset-cmd "bash dev-docker.sh reup"
python3 tools/probe_test.py concurrent --movie-id 950396 --concurrency 20 --reset-cmd "bash dev-docker.sh reup"

# полный прогон
python3 tools/probe_test.py all --movies 15 --reset-cmd "bash dev-docker.sh reup"
```

## Параметры

| Флаг | По умолчанию | Назначение |
|------|--------------|-----------|
| `--base`        | `http://localhost:9118` | URL инстанса lampac |
| `--email`       | `findz` | `account_email` в запросах |
| `--movie-id`    | — | tmdb id одного фильма (stability/cold/concurrent); без него берётся первый из каталога |
| `--movies`      | `20` | сколько фильмов взять из каталога |
| `--runs`        | `8` | прогонов в stability/cold |
| `--concurrency` | `20` | параллельных запросов в concurrent |
| `--reset-cmd`   | — | команда сброса кеша; для cold/concurrent обязательна |

## Сброс кеша

`cold` и `concurrent` требуют чистого кеша. `links` из `checkOnlineSearch` живёт в in-process
`IMemoryCache` — сбрасывается только рестартом инстанса. `--reset-cmd "bash dev-docker.sh reup"`
перезапускает dev-контейнер; скрипт сам ждёт готовности перед запросом.

Кеш парсинга в Postgres (`cache_entry`) при необходимости чистится отдельно:
`psql "$CONN" -c "TRUNCATE cache_entry, balanser_best"`.

## Exit code

`0` — аномалий нет · `1` — аномалии найдены · `2` — ошибка окружения (инстанс недоступен и т.п.).
Годится для CI/мониторинга.
