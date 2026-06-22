using Shared;
using System;
using System.Net.Http;
using System.Threading;

namespace Core.Services;

// Пока процесс жив и обслуживает запросы, раз в секунду пингуем наш бэкенд
// (hostHeartbeat.url). Бэкенд держит в памяти реестр активных lampac-хостов и
// отдаёт его фронту (backend .../routes/plugin.ts). Публичный хост бэкенд выводит
// из IP запроса (nip.io), поэтому тело пинга пустое — важен сам факт запроса.
//
// Отдельный лёгкий HttpClient (а не Shared Http) сознательно: пингу не нужны
// cloudflare-обработка/дефолтные заголовки/event-listeners, и он молча глотает
// ошибки, иначе недоступный бэкенд спамил бы лог раз в секунду.
public static class HostHeartbeat
{
    static readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2) // освежаем DNS на долгоживущем клиенте
    });

    static Timer _timer;
    static int _busy;

    public static void Start()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, CoreInit.conf.hostHeartbeat?.intervalSeconds ?? 1));

        // Таймер заводим всегда — enable/url читаются в Beat на каждом тике, чтобы
        // правка init.conf подхватывалась без перезапуска процесса.
        _timer = new Timer(Beat, null, interval, interval);
    }

    static async void Beat(object state)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            var conf = CoreInit.conf.hostHeartbeat;
            if (conf == null || !conf.enable || string.IsNullOrWhiteSpace(conf.url))
                return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, conf.timeoutSeconds)));
            using var content = new StringContent(string.Empty);
            using var resp = await _http.PostAsync(conf.url, content, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: бэкенд недоступен/таймаут/сеть — молча, попробуем на следующем тике.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
