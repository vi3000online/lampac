using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Npgsql;
using Shared.Services.Pools.Json;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Shared.Services.Hybrid;

public class PostgresHybridCache : BaseHybridCache, IHybridCache
{
    static IMemoryCache memoryCache;
    static string _connStr;
    static NpgsqlDataSource _dataSource;

    static Timer _statsFlushTimer, _gcTimer;
    static readonly ConcurrentDictionary<string, long> _hitBuffer = new();

    static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    static readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = false };

    public static int Stat_HitBufferCount => _hitBuffer.IsEmpty ? 0 : _hitBuffer.Count;

    public static NpgsqlDataSource DataSource => _dataSource;

    #region Configure
    public static void Configure(IMemoryCache mem)
    {
        memoryCache = mem;
    }

    public static void Initialize(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("cache.pg.connectionString is empty");

        _connStr = connectionString;
        _dataSource = NpgsqlDataSource.Create(connectionString);

        EnsureSchema();

        var pg = Shared.CoreInit.conf.cache.pg;
        _statsFlushTimer = new Timer(FlushHitStats, null,
            TimeSpan.FromSeconds(Math.Max(5, pg.statsFlushSeconds)),
            TimeSpan.FromSeconds(Math.Max(5, pg.statsFlushSeconds)));
        _gcTimer = new Timer(GcExpired, null,
            TimeSpan.FromSeconds(Math.Max(15, pg.gcSeconds)),
            TimeSpan.FromSeconds(Math.Max(15, pg.gcSeconds)));
    }

    static void EnsureSchema()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS cache_entry (
    key_hash    TEXT PRIMARY KEY,
    key_full    TEXT NOT NULL,
    plugin      TEXT NOT NULL,
    kind        TEXT NOT NULL,
    payload     BYTEA NOT NULL,
    is_text     BOOLEAN NOT NULL,
    text_json   BOOLEAN NOT NULL,
    capacity    INT NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL,
    hit_count   BIGINT NOT NULL DEFAULT 0,
    last_hit_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS cache_entry_expires_idx ON cache_entry (expires_at);
CREATE INDEX IF NOT EXISTS cache_entry_plugin_hits_idx ON cache_entry (plugin, hit_count DESC);

CREATE TABLE IF NOT EXISTS balanser_best (
    movie_key   TEXT PRIMARY KEY,
    payload     JSONB NOT NULL,
    any_ok      BOOLEAN NOT NULL,
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    hit_count   BIGINT NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS balanser_best_expires_idx ON balanser_best (expires_at);

CREATE TABLE IF NOT EXISTS events_cache (
    movie_key   TEXT PRIMARY KEY,
    payload     TEXT NOT NULL,
    work_count  INT NOT NULL,
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS events_cache_expires_idx ON events_cache (expires_at);
";
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
    #endregion

    #region helpers
    static (string plugin, string kind) ExtractMeta(string key)
    {
        if (string.IsNullOrEmpty(key))
            return ("", "");

        int first = key.IndexOf(':');
        if (first < 0)
            return (key, "");

        string plugin = key.Substring(0, first);
        int second = key.IndexOf(':', first + 1);
        string kind = second < 0
            ? key.Substring(first + 1)
            : key.Substring(first + 1, second - first - 1);

        return (plugin, kind);
    }

    static void BufferHit(string md5key)
    {
        _hitBuffer.AddOrUpdate(md5key, 1, static (_, prev) => prev + 1);
    }
    #endregion

    #region ContainsKey
    public bool ContainsKey<T>(string key, out T value)
        => ContainsKey(key, out value, out _);

    public bool ContainsKey<T>(string key, out T value, out DateTimeOffset ex)
    {
        if (memoryCache.TryGetValue(key, out T mv))
        {
            value = mv;
            ex = default;
            return true;
        }

        value = default;
        ex = default;

        try
        {
            string md5key = CrypTo.md5(key);
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(
                "SELECT expires_at FROM cache_entry WHERE key_hash = @k AND expires_at > now()", conn);
            cmd.Parameters.AddWithValue("k", md5key);
            var result = cmd.ExecuteScalar();
            if (result is DateTime dt)
            {
                ex = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "CatchId={CatchId}", "pg_containskey");
        }

        return false;
    }
    #endregion

    #region TryGetValue
    public bool TryGetValue<TItem>(string key, out TItem value, JsonTypeInfo<TItem> jsonType = null, bool textJson = false)
    {
        if (memoryCache.TryGetValue(key, out TItem mv))
        {
            value = mv;
            return true;
        }

        var entry = EntryAsync(key, fileCache: true, jsonType: jsonType, textJson: textJson).GetAwaiter().GetResult();
        if (entry != null && entry.success)
        {
            value = entry.value;
            return true;
        }

        value = default;
        return false;
    }
    #endregion

    #region EntryAsync
    public async Task<HybridCacheEntry<TItem>> EntryAsync<TItem>(string key, bool fileCache = false, JsonTypeInfo<TItem> jsonType = default, bool textJson = false)
    {
        if (!fileCache && memoryCache.TryGetValue(key, out TItem mv))
            return new HybridCacheEntry<TItem>(true, mv, false);

        var entry = await ReadCacheAsync(key, jsonType, textJson).ConfigureAwait(false);
        if (entry.success)
            return new HybridCacheEntry<TItem>(true, entry.value, entry.singleCache);

        return new HybridCacheEntry<TItem>(false, default, false);
    }
    #endregion

    #region ReadCacheAsync
    async Task<(bool success, TItem value, bool singleCache)> ReadCacheAsync<TItem>(string key, JsonTypeInfo<TItem> jsonType, bool textJson)
    {
        string md5key = CrypTo.md5(key);

        try
        {
            var type = typeof(TItem);
            bool isText = TypeCache<TItem>.IsText;
            bool isDeserialize = textJson || jsonType != default || TypeCache<TItem>.IsDeserializable;
            if (!isText && !isDeserialize)
                return default;

            byte[] payload;
            bool storedIsText;
            int capacity;
            DateTimeOffset ex;

            await using (var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false))
            await using (var cmd = new NpgsqlCommand(
                "SELECT payload, is_text, capacity, expires_at FROM cache_entry WHERE key_hash = @k AND expires_at > now()", conn))
            {
                cmd.Parameters.AddWithValue("k", md5key);
                await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (!await reader.ReadAsync().ConfigureAwait(false))
                    return default;

                payload = (byte[])reader["payload"];
                storedIsText = reader.GetBoolean(reader.GetOrdinal("is_text"));
                capacity = reader.GetInt32(reader.GetOrdinal("capacity"));
                var dt = reader.GetFieldValue<DateTime>(reader.GetOrdinal("expires_at"));
                ex = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }

            BufferHit(md5key);

            TItem value;
            if (isDeserialize && !storedIsText)
            {
                if (textJson || jsonType != default)
                {
                    using var ms = new MemoryStream(payload, writable: false);
                    await using var gzip = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: false);
                    value = jsonType != default
                        ? await System.Text.Json.JsonSerializer.DeserializeAsync(gzip, jsonType).ConfigureAwait(false)
                        : await System.Text.Json.JsonSerializer.DeserializeAsync<TItem>(gzip).ConfigureAwait(false);
                }
                else
                {
                    using var ms = new MemoryStream(payload, writable: false);
                    await using var gzip = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: false);
                    using var sr = new StreamReader(gzip, Encoding.UTF8);
                    using var jr = new JsonTextReader(sr) { ArrayPool = NewtonsoftPool.Array };
                    var serializer = Newtonsoft.Json.JsonSerializer.CreateDefault();

                    if (IsCapacityCollection(type) && capacity > 0)
                    {
                        var instance = CreateCollectionWithCapacity(type, capacity);
                        if (instance != null)
                        {
                            serializer.Populate(jr, instance);
                            value = (TItem)instance;
                        }
                        else
                        {
                            value = serializer.Deserialize<TItem>(jr);
                        }
                    }
                    else
                    {
                        value = serializer.Deserialize<TItem>(jr);
                    }
                }
            }
            else
            {
                string raw = _utf8NoBom.GetString(payload);
                if (typeof(TItem) == typeof(string))
                    value = (TItem)(object)raw;
                else
                    value = (TItem)Convert.ChangeType(raw, typeof(TItem), CultureInfo.InvariantCulture);
            }

            if (value is null)
                return default;

            bool singleCache = true;
            if (Shared.CoreInit.conf.cache.memExtend && Shared.CoreInit.conf.cache.extend > 0)
            {
                singleCache = false;
                var targetEx = DateTimeOffset.Now.AddSeconds(Shared.CoreInit.conf.cache.extend);
                memoryCache.Set(key, value, targetEx > ex ? ex : targetEx);
            }

            return (true, value, singleCache);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CatchId={CatchId}", "pg_read");
        }

        return default;
    }
    #endregion

    #region Set
    public TItem Set<TItem>(string key, TItem value, DateTimeOffset absoluteExpiration, bool? inmemory = null, bool textJson = false)
    {
        if (inmemory != true && Shared.CoreInit.conf.cache.type != "mem" && WriteCache(key, value, absoluteExpiration, textJson))
            return value;

        return memoryCache.Set(key, value, absoluteExpiration);
    }

    public TItem Set<TItem>(string key, TItem value, TimeSpan absoluteExpirationRelativeToNow, bool? inmemory = null, bool textJson = false)
    {
        var ex = DateTimeOffset.Now.Add(absoluteExpirationRelativeToNow == default ? TimeSpan.FromMinutes(1) : absoluteExpirationRelativeToNow);
        return Set(key, value, ex, inmemory, textJson);
    }
    #endregion

    #region WriteCache
    bool WriteCache<TItem>(string key, TItem value, DateTimeOffset absoluteExpiration, bool textJson)
    {
        try
        {
            var now = DateTimeOffset.Now;
            if (absoluteExpiration <= now)
                return false;

            // короткоживущие ключи держим только в памяти, не нагружаем БД
            var minLifetime = TimeSpan.FromSeconds(Math.Max(15, Shared.CoreInit.conf.cache.extend) + 60);
            if (absoluteExpiration <= now.Add(minLifetime))
            {
                memoryCache.Set(key, value, absoluteExpiration);
                return true;
            }

            var type = typeof(TItem);
            bool isText = TypeCache<TItem>.IsText;
            bool isSerialize = textJson || TypeCache<TItem>.IsDeserializable;
            if (!isText && !isSerialize)
                return false;

            byte[] payload;
            int capacity = 0;
            bool storedIsText = isText && !textJson && !TypeCache<TItem>.IsDeserializable;

            if (storedIsText)
            {
                payload = _utf8NoBom.GetBytes((string)(object)value);
            }
            else
            {
                capacity = GetCapacity(value);
                using var ms = PoolInvk.msm.GetStream();
                using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    if (textJson)
                    {
                        System.Text.Json.JsonSerializer.Serialize(gzip, value, _jsonSerializerOptions);
                    }
                    else
                    {
                        using var sw = new StreamWriter(gzip, _utf8NoBom, PoolInvk.bufferSize, leaveOpen: true);
                        using var jw = new JsonTextWriter(sw)
                        {
                            Formatting = Formatting.None,
                            ArrayPool = NewtonsoftPool.Array
                        };
                        var serializer = Newtonsoft.Json.JsonSerializer.CreateDefault();
                        serializer.Serialize(jw, value);
                    }
                }
                payload = ms.ToArray();
            }

            var (plugin, kind) = ExtractMeta(key);
            string md5key = CrypTo.md5(key);

            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
INSERT INTO cache_entry (key_hash, key_full, plugin, kind, payload, is_text, text_json, capacity, expires_at)
VALUES (@k, @kf, @p, @kd, @pl, @it, @tj, @cap, @ex)
ON CONFLICT (key_hash) DO UPDATE SET
    payload = EXCLUDED.payload,
    is_text = EXCLUDED.is_text,
    text_json = EXCLUDED.text_json,
    capacity = EXCLUDED.capacity,
    expires_at = EXCLUDED.expires_at,
    created_at = now()", conn);
            cmd.Parameters.AddWithValue("k", md5key);
            cmd.Parameters.AddWithValue("kf", key);
            cmd.Parameters.AddWithValue("p", plugin);
            cmd.Parameters.AddWithValue("kd", kind);
            cmd.Parameters.AddWithValue("pl", payload);
            cmd.Parameters.AddWithValue("it", storedIsText);
            cmd.Parameters.AddWithValue("tj", textJson);
            cmd.Parameters.AddWithValue("cap", capacity);
            cmd.Parameters.AddWithValue("ex", absoluteExpiration.UtcDateTime);
            cmd.ExecuteNonQuery();

            // также кладём в memory L1
            memoryCache.Set(key, value, absoluteExpiration);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CatchId={CatchId}", "pg_write");
            return false;
        }
    }
    #endregion

    #region FlushHitStats
    static int _flushing = 0;

    static void FlushHitStats(object _)
    {
        if (_hitBuffer.IsEmpty) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;

        try
        {
            var snapshot = new List<(string key, long count)>(_hitBuffer.Count);
            foreach (var kv in _hitBuffer)
            {
                if (_hitBuffer.TryRemove(kv.Key, out var n))
                    snapshot.Add((kv.Key, n));
            }
            if (snapshot.Count == 0) return;

            using var conn = _dataSource.OpenConnection();
            using var tx = conn.BeginTransaction();
            using var cmd = new NpgsqlCommand(@"
UPDATE cache_entry
SET hit_count = hit_count + @n, last_hit_at = now()
WHERE key_hash = @k", conn, tx);
            var pk = cmd.Parameters.Add("k", NpgsqlTypes.NpgsqlDbType.Text);
            var pn = cmd.Parameters.Add("n", NpgsqlTypes.NpgsqlDbType.Bigint);
            cmd.Prepare();
            foreach (var (k, n) in snapshot)
            {
                pk.Value = k;
                pn.Value = n;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception e)
        {
            Log.Error(e, "CatchId={CatchId}", "pg_flush_hits");
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }
    #endregion

    #region GC
    static int _gcing = 0;

    static void GcExpired(object _)
    {
        if (Interlocked.Exchange(ref _gcing, 1) == 1) return;
        try
        {
            int grace = Math.Max(0, Shared.CoreInit.conf.cache.pg.gcGraceMinutes);
            using var conn = _dataSource.OpenConnection();
            using (var cmd = new NpgsqlCommand(
                $"DELETE FROM cache_entry WHERE expires_at < now() - interval '{grace} minutes'", conn))
                cmd.ExecuteNonQuery();
            using (var cmd2 = new NpgsqlCommand(
                $"DELETE FROM balanser_best WHERE expires_at < now() - interval '{grace} minutes'", conn))
                cmd2.ExecuteNonQuery();
            using (var cmd3 = new NpgsqlCommand(
                $"DELETE FROM events_cache WHERE expires_at < now() - interval '{grace} minutes'", conn))
                cmd3.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Error(e, "CatchId={CatchId}", "pg_gc");
        }
        finally
        {
            Volatile.Write(ref _gcing, 0);
        }
    }
    #endregion

    #region events_cache (общий кеш агрегации /lite/events)
    // Возвращает payload собранного списка источников, общий для всех инстансов, или null.
    public static async Task<string> ReadEventsAsync(string movieKey)
    {
        if (_dataSource == null) return null;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "SELECT payload FROM events_cache WHERE movie_key = @k AND expires_at > now()", conn);
            cmd.Parameters.AddWithValue("k", movieKey);
            return (await cmd.ExecuteScalarAsync().ConfigureAwait(false)) as string;
        }
        catch (Exception e)
        {
            Log.Error(e, "CatchId={CatchId}", "pg_events_read");
            return null;
        }
    }

    // UPSERT с merge: общий результат заменяется только если новый не беднее (work_count),
    // либо если текущий уже протух. Возвращает актуальный (после merge) payload.
    public static async Task<string> WriteEventsMergeAsync(string movieKey, string payload, int workCount, DateTimeOffset expiresAt)
    {
        if (_dataSource == null) return null;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(@"
INSERT INTO events_cache (movie_key, payload, work_count, expires_at)
VALUES (@k, @p, @wc, @ex)
ON CONFLICT (movie_key) DO UPDATE SET
    payload    = CASE WHEN EXCLUDED.work_count >= events_cache.work_count OR events_cache.expires_at < now()
                      THEN EXCLUDED.payload ELSE events_cache.payload END,
    work_count = CASE WHEN EXCLUDED.work_count >= events_cache.work_count OR events_cache.expires_at < now()
                      THEN EXCLUDED.work_count ELSE events_cache.work_count END,
    expires_at = CASE WHEN EXCLUDED.work_count >= events_cache.work_count OR events_cache.expires_at < now()
                      THEN EXCLUDED.expires_at ELSE events_cache.expires_at END,
    updated_at = now()
RETURNING payload", conn);
            cmd.Parameters.AddWithValue("k", movieKey);
            cmd.Parameters.AddWithValue("p", payload);
            cmd.Parameters.AddWithValue("wc", workCount);
            cmd.Parameters.AddWithValue("ex", expiresAt.UtcDateTime);
            return (await cmd.ExecuteScalarAsync().ConfigureAwait(false)) as string;
        }
        catch (Exception e)
        {
            Log.Error(e, "CatchId={CatchId}", "pg_events_write");
            return null;
        }
    }
    #endregion
}
