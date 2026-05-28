using Npgsql;
using NpgsqlTypes;
using Shared.Services.Hybrid;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

namespace Shared.Services.BestBalanser;

public sealed record BalanserCandidate(string plugin, string name, string url);

public static class BestBalanserService
{
    sealed class CacheEntry
    {
        public Dictionary<string, BalanserHealth> results;
        public DateTime expiresAt;
    }

    static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    static readonly ConcurrentDictionary<string, Task<Dictionary<string, BalanserHealth>>> _inflight = new();

    static bool PgMode => CoreInit.conf.cache?.type == "pg" && PostgresHybridCache.DataSource != null;

    public static string BuildKey(string imdb_id, long kinopoisk_id, long tmdb_id, int serial)
    {
        return $"{imdb_id}|{kinopoisk_id}|{tmdb_id}|{serial}";
    }

    public static Dictionary<string, BalanserHealth> Peek(string key)
    {
        if (_cache.TryGetValue(key, out var ce) && ce.expiresAt > DateTime.UtcNow)
            return ce.results;
        return null;
    }

    public static Task<Dictionary<string, BalanserHealth>> RunOrJoinAsync(
        string key,
        IReadOnlyList<BalanserCandidate> candidates,
        bool isSerial,
        Dictionary<string, string> loopbackHeaders,
        int totalTimeoutMs,
        int perProbeTimeoutMs,
        int speedSamples,
        int maxRetries,
        int successCacheMinutes,
        int failureCacheMinutes,
        CancellationToken ct)
    {
        var cached = Peek(key);
        if (cached != null)
            return Task.FromResult(cached);

        return _inflight.GetOrAdd(key, _ => RunInternalAsync(
            key, candidates, isSerial, loopbackHeaders,
            totalTimeoutMs, perProbeTimeoutMs, speedSamples, maxRetries,
            successCacheMinutes, failureCacheMinutes, ct));
    }

    static async Task<Dictionary<string, BalanserHealth>> RunInternalAsync(
        string key,
        IReadOnlyList<BalanserCandidate> candidates,
        bool isSerial,
        Dictionary<string, string> loopbackHeaders,
        int totalTimeoutMs,
        int perProbeTimeoutMs,
        int speedSamples,
        int maxRetries,
        int successCacheMinutes,
        int failureCacheMinutes,
        CancellationToken ct)
    {
        try
        {
            if (PgMode)
            {
                var fromPg = await TryReadPgAsync(key).ConfigureAwait(false);
                if (fromPg != null)
                {
                    PutLocal(key, fromPg.results, fromPg.expiresAt);
                    return fromPg.results;
                }
            }

            IAsyncDisposable distLock = null;
            try
            {
                if (PgMode)
                {
                    var dl = HybridCache.GetDistributedLock();
                    if (dl != null)
                        distLock = await dl.AcquireAsync($"bestbalanser:{key}",
                            TimeSpan.FromMilliseconds(CoreInit.conf.cache.pg.advisoryLockTimeoutMs)).ConfigureAwait(false);

                    var fromPg = await TryReadPgAsync(key).ConfigureAwait(false);
                    if (fromPg != null)
                    {
                        PutLocal(key, fromPg.results, fromPg.expiresAt);
                        return fromPg.results;
                    }
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(totalTimeoutMs);

                var tasks = candidates
                    .Select(c => SpeedProbe.RunAsync(c.plugin, c.name, c.url, isSerial, loopbackHeaders,
                        perProbeTimeoutMs, speedSamples, maxRetries, cts.Token))
                    .ToArray();

                BalanserHealth[] healths;
                try
                {
                    healths = await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch
                {
                    healths = tasks
                        .Where(t => t.IsCompletedSuccessfully)
                        .Select(t => t.Result)
                        .ToArray();
                }

                var dict = new Dictionary<string, BalanserHealth>(healths.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var h in healths.Where(h => h != null))
                    dict[h.plugin] = h;

                bool anyOk = dict.Values.Any(h => h.isWorking);
                int ttlMin = anyOk ? successCacheMinutes : failureCacheMinutes;
                var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, ttlMin));

                if (PgMode)
                {
                    var merged = await TryWritePgAsync(key, dict, anyOk, expiresAt).ConfigureAwait(false);
                    if (merged != null)
                    {
                        PutLocal(key, merged.results, merged.expiresAt);
                        return merged.results;
                    }
                }

                PutLocal(key, dict, expiresAt);
                return dict;
            }
            finally
            {
                if (distLock != null)
                    await distLock.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public static void Invalidate(string key)
    {
        _cache.TryRemove(key, out _);

        if (PgMode)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var conn = await PostgresHybridCache.DataSource.OpenConnectionAsync().ConfigureAwait(false);
                    await using var cmd = new NpgsqlCommand("DELETE FROM balanser_best WHERE movie_key = @k", conn);
                    cmd.Parameters.AddWithValue("k", key);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "BestBalanser pg invalidate failed");
                }
            });
        }
    }

    #region pg helpers
    sealed record PgEntry(Dictionary<string, BalanserHealth> results, DateTime expiresAt);

    static void PutLocal(string key, Dictionary<string, BalanserHealth> dict, DateTime expiresAt)
    {
        _cache[key] = new CacheEntry { results = dict, expiresAt = expiresAt };
    }

    static async Task<PgEntry> TryReadPgAsync(string key)
    {
        try
        {
            await using var conn = await PostgresHybridCache.DataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "SELECT payload::text, expires_at FROM balanser_best WHERE movie_key = @k AND expires_at > now()", conn);
            cmd.Parameters.AddWithValue("k", key);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return null;

            var payloadJson = reader.GetString(0);
            var ex = reader.GetFieldValue<DateTime>(1);

            var dict = JsonSerializer.Deserialize<Dictionary<string, BalanserHealth>>(payloadJson);
            if (dict == null)
                return null;

            // Поверх читателя: атомарно увеличить hit_count (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var c2 = await PostgresHybridCache.DataSource.OpenConnectionAsync().ConfigureAwait(false);
                    await using var u = new NpgsqlCommand("UPDATE balanser_best SET hit_count = hit_count + 1 WHERE movie_key = @k", c2);
                    u.Parameters.AddWithValue("k", key);
                    await u.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch { }
            });

            return new PgEntry(dict, DateTime.SpecifyKind(ex, DateTimeKind.Utc));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "BestBalanser pg read failed for {Key}", key);
            return null;
        }
    }

    static async Task<PgEntry> TryWritePgAsync(string key, Dictionary<string, BalanserHealth> dict, bool anyOk, DateTime expiresAt)
    {
        try
        {
            string payloadJson = JsonSerializer.Serialize(dict);

            await using var conn = await PostgresHybridCache.DataSource.OpenConnectionAsync().ConfigureAwait(false);
            // JSONB || объединяет: правый перезаписывает левый по совпадающим ключам,
            // уникальные ключи с обеих сторон сохраняются. Так 5+10 = объединение,
            // и новый замер плагина перезаписывает устаревший.
            // RETURNING отдаёт уже смерженный payload — чтобы вызывающий инстанс
            // тоже увидел полную картину, а не только свой замер.
            await using var cmd = new NpgsqlCommand(@"
INSERT INTO balanser_best (movie_key, payload, any_ok, expires_at)
VALUES (@k, @p::jsonb, @ok, @ex)
ON CONFLICT (movie_key) DO UPDATE SET
    payload    = balanser_best.payload || EXCLUDED.payload,
    any_ok     = balanser_best.any_ok OR EXCLUDED.any_ok,
    expires_at = GREATEST(balanser_best.expires_at, EXCLUDED.expires_at),
    updated_at = now()
RETURNING payload::text, expires_at", conn);
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = key });
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = payloadJson });
            cmd.Parameters.Add(new NpgsqlParameter("ok", NpgsqlDbType.Boolean) { Value = anyOk });
            cmd.Parameters.Add(new NpgsqlParameter("ex", NpgsqlDbType.TimestampTz) {
                Value = expiresAt.Kind == DateTimeKind.Utc ? expiresAt : expiresAt.ToUniversalTime()
            });

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return null;

            var mergedJson = reader.GetString(0);
            var mergedEx = reader.GetFieldValue<DateTime>(1);
            var merged = JsonSerializer.Deserialize<Dictionary<string, BalanserHealth>>(mergedJson);
            if (merged == null)
                return null;

            return new PgEntry(merged, DateTime.SpecifyKind(mergedEx, DateTimeKind.Utc));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "BestBalanser pg write failed for {Key}", key);
            return null;
        }
    }
    #endregion
}
