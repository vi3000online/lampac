-- 001_cache_entry.down.sql
-- Откат: удаляет таблицу cache_entry и её индексы.

DROP INDEX IF EXISTS cache_entry_plugin_hits_idx;
DROP INDEX IF EXISTS cache_entry_expires_idx;
DROP TABLE IF EXISTS cache_entry;
