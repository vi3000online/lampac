-- 002_balanser_best.up.sql
-- Кеш вердиктов BestBalanserService: какие плагины работают для фильма + метрики скорости.
-- Одна запись на фильм, payload — JSONB-словарь {plugin: BalanserHealth}.
-- Запись обновляется через UPSERT с JSONB || merge (см. BestBalanserService.TryWritePgAsync).
-- Идемпотентна.

CREATE TABLE IF NOT EXISTS balanser_best (
    movie_key   TEXT        PRIMARY KEY,                   -- "imdb|kp|tmdb|serial"
    payload     JSONB       NOT NULL,                      -- {"filmix": {...}, "rezka": {...}, ...}
    any_ok      BOOLEAN     NOT NULL,                      -- хоть один плагин рабочий
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    hit_count   BIGINT      NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS balanser_best_expires_idx
    ON balanser_best (expires_at);

COMMENT ON TABLE  balanser_best IS 'Кеш вердиктов SpeedProbe по фильмам. Шарится между инстансами через JSONB merge.';
COMMENT ON COLUMN balanser_best.movie_key IS 'BestBalanserService.BuildKey(): "imdb|kp|tmdb|serial".';
COMMENT ON COLUMN balanser_best.payload   IS 'JSONB-словарь {plugin: BalanserHealth}. Обновляется через ||  (right-bias merge).';
COMMENT ON COLUMN balanser_best.any_ok    IS 'true если хотя бы один плагин в payload имеет isWorking=true.';
