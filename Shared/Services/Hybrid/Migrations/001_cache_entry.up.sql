-- 001_cache_entry.up.sql
-- Главная таблица распределённого кеша балансировщиков (результат парсинга upstream).
-- Идемпотентна: можно прогонять повторно.

CREATE TABLE IF NOT EXISTS cache_entry (
    key_hash    TEXT        PRIMARY KEY,                   -- md5 от полного ключа
    key_full    TEXT        NOT NULL,                      -- "plugin:kind:..." (для аналитики)
    plugin      TEXT        NOT NULL,                      -- "kinobase", "filmix", ...
    kind        TEXT        NOT NULL,                      -- "view", "search", ...
    payload     BYTEA       NOT NULL,                      -- GZip-сжатый JSON/text
    is_text     BOOLEAN     NOT NULL,
    text_json   BOOLEAN     NOT NULL,
    capacity    INT         NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL,
    hit_count   BIGINT      NOT NULL DEFAULT 0,
    last_hit_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS cache_entry_expires_idx
    ON cache_entry (expires_at);

CREATE INDEX IF NOT EXISTS cache_entry_plugin_hits_idx
    ON cache_entry (plugin, hit_count DESC);

COMMENT ON TABLE  cache_entry IS 'Распределённый кеш результатов парсинга балансировщиков (общий между инстансами lampac).';
COMMENT ON COLUMN cache_entry.key_hash IS 'MD5 от key_full, первичный ключ.';
COMMENT ON COLUMN cache_entry.key_full IS 'Человекочитаемый ключ "plugin:kind:params". Для аналитики.';
COMMENT ON COLUMN cache_entry.payload  IS 'GZip-сжатый payload (BYTEA). is_text/text_json указывают на тип контента.';
