using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Shared.Services.Hybrid;

public class PostgresDistributedLock : IDistributedLock
{
    static readonly Serilog.ILogger Log = Serilog.Log.ForContext<PostgresDistributedLock>();

    public async Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout)
    {
        var ds = PostgresHybridCache.DataSource;
        if (ds == null) return null;

        long lockId = HashTo64(key);
        NpgsqlConnection conn = null;
        NpgsqlTransaction tx = null;

        try
        {
            conn = await ds.OpenConnectionAsync().ConfigureAwait(false);
            tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

            // SET LOCAL живёт только внутри транзакции и откатывается вместе с ней —
            // не нужно вручную возвращать statement_timeout, пуловое соединение не «пачкается».
            int ms = (int)Math.Max(1000, timeout.TotalMilliseconds);
            await using (var st = new NpgsqlCommand($"SET LOCAL statement_timeout = {ms}", conn, tx))
                await st.ExecuteNonQueryAsync().ConfigureAwait(false);

            // pg_advisory_xact_lock — транзакционный лок: освобождается АВТОМАТИЧЕСКИ
            // при commit/rollback. Даже если Releaser не задиспозят, возврат соединения
            // в пул откатит транзакцию и снимет лок — утечка невозможна.
            await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@id)", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", lockId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            return new Releaser(conn, tx);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "advisory_xact_lock failed for {Key}", key);
            // Порядок важен: сперва откат транзакции (снимет лок, если успел взяться),
            // затем возврат соединения в пул.
            if (tx != null)
            {
                try { await tx.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (conn != null)
            {
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            return null;
        }
    }

    static long HashTo64(string s)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(s), hash);
        return BitConverter.ToInt64(hash);
    }

    sealed class Releaser : IAsyncDisposable
    {
        readonly NpgsqlConnection _conn;
        readonly NpgsqlTransaction _tx;
        int _disposed = 0;

        public Releaser(NpgsqlConnection conn, NpgsqlTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            // Commit завершает транзакцию и снимает xact-лок. Если commit упадёт —
            // Dispose транзакции/соединения всё равно откатит её, и лок освободится.
            try
            {
                await _tx.CommitAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "advisory lock tx commit failed");
            }
            finally
            {
                try { await _tx.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await _conn.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }
}
