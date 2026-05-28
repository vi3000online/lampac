-- Общий кеш агрегации /lite/events: один фильм — один результат для всех инстансов.
-- Запись через merge: бедный результат не вытесняет богатый (см. WriteEventsMergeAsync).

CREATE TABLE IF NOT EXISTS events_cache (
    movie_key   TEXT PRIMARY KEY,
    payload     TEXT NOT NULL,
    work_count  INT NOT NULL,
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS events_cache_expires_idx ON events_cache (expires_at);

COMMENT ON TABLE  events_cache            IS 'Общий кеш собранного списка источников /lite/events';
COMMENT ON COLUMN events_cache.movie_key  IS 'events:{id}:{serial}:{source}';
COMMENT ON COLUMN events_cache.payload    IS 'JSON-сериализованный List<EventLinkItem>';
COMMENT ON COLUMN events_cache.work_count IS 'Кол-во рабочих источников — критерий merge';
