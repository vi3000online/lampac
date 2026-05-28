-- 002_balanser_best.down.sql
-- Откат: удаляет таблицу balanser_best и её индексы.

DROP INDEX IF EXISTS balanser_best_expires_idx;
DROP TABLE IF EXISTS balanser_best;
