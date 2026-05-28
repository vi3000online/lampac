namespace Shared.Services.Hybrid;

public interface IDistributedLock
{
    /// <summary>
    /// Берёт распределённый лок по ключу. Возвращает IAsyncDisposable, при Dispose лок освобождается.
    /// Если лок взять не удалось за таймаут — возвращает null.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout);
}
