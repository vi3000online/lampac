using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Shared.Services.BestBalanser;

public static class SpeedProbe
{
    static readonly HttpClient _client;

    static readonly Regex _qualityRx = new("(\\d{3,4})p?", RegexOptions.Compiled);

    static readonly string[] _streamExt = [".m3u8", ".mp4", ".m4s", ".ts", ".mkv"];

    const int ProbeBytes = 10 * 1048576; // 10 MB — cap: download up to this much per speed sample
    const int MinValidBytes = 1048576;   // 1 MB — minimum downloaded for a sample to count as valid

    const string RCH_MARKER = "__rch__";

    const int RetryBackoffMs = 200;   // base backoff between retries (linear: 200, 400, 600...)
    const int MinUsefulMs = 350;      // skip a step if less than this remains before the deadline

    // Контекст одной пробы: бюджет времени + параметры повторов/замеров.
    sealed record ProbeCtx(int MaxRetries, int SpeedSamples, DateTime DeadlineUtc, int TotalTimeoutMs)
    {
        public int ResolveStepMs => Math.Max(1500, TotalTimeoutMs / 3);
        public int ProbeStepMs => Math.Max(2000, TotalTimeoutMs / 2);
    }

    static SpeedProbe()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task<BalanserHealth> RunAsync(
        string plugin,
        string name,
        string balanserUrl,
        bool isSerial,
        Dictionary<string, string> loopbackHeaders,
        int totalTimeoutMs,
        int speedSamples,
        int maxRetries,
        CancellationToken outerToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(totalTimeoutMs);

        var ctx = new ProbeCtx(
            MaxRetries: Math.Max(0, maxRetries),
            SpeedSamples: Math.Max(1, speedSamples),
            DeadlineUtc: DateTime.UtcNow.AddMilliseconds(totalTimeoutMs),
            TotalTimeoutMs: totalTimeoutMs);

        try
        {
            var (streamUrl, streamHeaders, quality, qScore) =
                await ResolveStreamAsync(balanserUrl, isSerial, loopbackHeaders, ctx, cts.Token).ConfigureAwait(false);

            if (streamUrl == RCH_MARKER)
                return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isRch = true, isWorking = false, error = "rch" };

            if (string.IsNullOrEmpty(streamUrl))
                return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "no_stream_url" };

            // Some balansers return a chained URL like /lite/phantom/video?... that itself
            // resolves to another JSON. Follow up to 6 hops to reach the real stream
            // (multi-level trees: озвучка → сезон → серия → стрим).
            int hops = 0;
            while (IsLampacInternalEndpoint(streamUrl) && hops < 6)
            {
                var (next, nextHeaders, nextQuality, nextScore) =
                    await ResolveStreamFromUrlAsync(streamUrl, loopbackHeaders, ctx, cts.Token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(next) || next == streamUrl || next == RCH_MARKER) break;

                streamUrl = next;
                if (nextHeaders != null) streamHeaders = nextHeaders;
                if (!string.IsNullOrEmpty(nextQuality) && nextScore > qScore)
                {
                    quality = nextQuality;
                    qScore = nextScore;
                }
                hops++;
            }

            if (IsLampacInternalEndpoint(streamUrl))
                return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "internal_loop" };

            Console.WriteLine($"[best-probe] {plugin,-15} picked quality={quality ?? "?"} qScore={qScore} hops={hops} url={(streamUrl.Length > 80 ? streamUrl.Substring(0, 80) + "..." : streamUrl)}");

            string playableUrl = streamUrl;
            var playableHeaders = streamHeaders;

            if (IsM3u8(streamUrl))
            {
                var (manifest, manifestHeaders) = await GetTextAsync(streamUrl, streamHeaders, ctx, cts.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(manifest))
                    return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "manifest_empty" };

                var (next, m3u8Quality, isVariant) = PickBestSegmentOrVariant(manifest, streamUrl);
                if (string.IsNullOrEmpty(next))
                    return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "no_segment" };

                // Override quality from master m3u8 if not detected from JSON.
                if (m3u8Quality != null && (string.IsNullOrEmpty(quality) || qScore == 0))
                {
                    quality = m3u8Quality;
                    qScore = QualityWeight(m3u8Quality);
                }

                if (isVariant)
                {
                    // It was a master playlist — descend into the picked variant and take its first segment.
                    var (vManifest, _) = await GetTextAsync(next, manifestHeaders, ctx, cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(vManifest))
                        return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "variant_manifest_empty" };

                    var (segUrl, _, _) = PickBestSegmentOrVariant(vManifest, next);
                    if (string.IsNullOrEmpty(segUrl))
                        return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "no_segment_in_variant" };

                    playableUrl = segUrl;
                }
                else
                {
                    playableUrl = next;
                }

                playableHeaders = manifestHeaders;
            }

            var (ttfb, throughput, bytesRead, ok, errorCode) =
                await MeasureSpeedAsync(playableUrl, playableHeaders, ctx, cts.Token).ConfigureAwait(false);

            if (!ok)
                return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = errorCode };

            return new BalanserHealth
            {
                plugin = plugin,
                name = name,
                url = balanserUrl,
                isWorking = true,
                qualityScore = qScore,
                quality = quality,
                ttfbSeconds = ttfb,
                throughputBytesPerSec = throughput,
                bytesRead = bytesRead
            };
        }
        catch (OperationCanceledException)
        {
            return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = "timeout" };
        }
        catch (Exception ex)
        {
            return new BalanserHealth { plugin = plugin, name = name, url = balanserUrl, isWorking = false, error = ex.GetType().Name };
        }
    }

    #region speed measurement

    // N независимых замеров скорости. Каждый замер при timeout/transient-ошибке
    // повторяется до ctx.MaxRetries раз. Итог — медиана throughput/ttfb по успешным
    // замерам: один шумный замер (TCP-всплеск, пакетная потеря) не искажает результат.
    static async Task<(double ttfb, double throughput, long bytesRead, bool ok, string error)>
        MeasureSpeedAsync(string url, Dictionary<string, string> headers, ProbeCtx ctx, CancellationToken ct)
    {
        var ttfbs = new List<double>();
        var rates = new List<double>();
        long bytesAcc = 0;
        string lastError = null;

        for (int s = 0; s < ctx.SpeedSamples; s++)
        {
            if (BudgetExhausted(ctx)) break;

            var r = await SingleRangeProbeAsync(url, headers, ctx, ct).ConfigureAwait(false);

            if (r.ok)
            {
                Accumulate(r, ttfbs, rates, ref bytesAcc);
                continue;
            }

            lastError = r.error;

            // Жёсткая ошибка (http_4xx, неизвестное исключение) — повтор бесполезен.
            if (!IsRetriable(r.error))
            {
                if (ttfbs.Count == 0)
                    return (0, 0, 0, false, r.error);
                break;
            }

            // Transient — повторяем именно этот замер.
            for (int retry = 0; retry < ctx.MaxRetries; retry++)
            {
                if (BudgetExhausted(ctx)) break;
                await DelayBackoffAsync(retry, ct).ConfigureAwait(false);

                var rr = await SingleRangeProbeAsync(url, headers, ctx, ct).ConfigureAwait(false);
                if (rr.ok)
                {
                    Accumulate(rr, ttfbs, rates, ref bytesAcc);
                    break;
                }
                lastError = rr.error;
                if (!IsRetriable(rr.error)) break;
            }
        }

        if (ttfbs.Count == 0)
            return (0, 0, 0, false, lastError ?? "timeout");

        double medTtfb = Median(ttfbs);
        double medRate = rates.Count > 0 ? Median(rates) : 0;
        long avgBytes = bytesAcc / ttfbs.Count;
        return (medTtfb, medRate, avgBytes, true, null);
    }

    static void Accumulate(
        (double ttfb, long bytes, double sec, bool ok, string error) r,
        List<double> ttfbs, List<double> rates, ref long bytesAcc)
    {
        ttfbs.Add(r.ttfb);
        if (r.sec > 0 && r.bytes > 0)
            rates.Add(r.bytes / r.sec);
        bytesAcc += r.bytes;
    }

    // Один range-замер: качаем до ProbeBytes (либо пока не истечёт окно замера),
    // меряем ttfb и пропускную способность. Если окно истекло, но успели скачать
    // не меньше MinValidBytes — замер считается валидным по фактически скачанному.
    static async Task<(double ttfb, long bytes, double sec, bool ok, string error)>
        SingleRangeProbeAsync(string url, Dictionary<string, string> headers, ProbeCtx ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var step = StepCts(ct, ctx.DeadlineUtc, ctx.ProbeStepMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(req, headers);
            req.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, step.Token).ConfigureAwait(false);
            double ttfb = sw.Elapsed.TotalSeconds;

            if ((int)resp.StatusCode >= 400)
                return (ttfb, 0, 0, false, $"http_{(int)resp.StatusCode}");

            await using var stream = await resp.Content.ReadAsStreamAsync(step.Token).ConfigureAwait(false);
            byte[] buf = new byte[65536];
            long total = 0;
            bool windowExpired = false;
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf, step.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                    if (total >= ProbeBytes) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Истекло окно замера (не внешний дедлайн) — частично скачанное ещё может быть валидным.
                windowExpired = true;
            }

            sw.Stop();
            double sec = sw.Elapsed.TotalSeconds;

            if (total <= 0)
                return (ttfb, 0, sec, false, "empty_body");

            // Скачали достаточно для надёжной оценки пропускной способности.
            if (total >= MinValidBytes)
                return (ttfb, total, sec, true, null);

            // Слишком мало данных — замер ненадёжен.
            return (ttfb, total, sec, false, windowExpired ? "timeout" : "empty_body");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Внешний дедлайн / клиент ушёл — повтор бессмыслен.
            return (sw.Elapsed.TotalSeconds, 0, 0, false, "canceled");
        }
        catch (OperationCanceledException)
        {
            // Истёк per-step таймаут до начала чтения тела — ошибка retriable.
            return (sw.Elapsed.TotalSeconds, 0, 0, false, "timeout");
        }
        catch (Exception ex)
        {
            return (sw.Elapsed.TotalSeconds, 0, 0, false, ex.GetType().Name);
        }
    }

    #endregion

    #region retry helpers

    // Linked CTS с таймаутом одного сетевого шага, ограниченным остатком общего бюджета.
    static CancellationTokenSource StepCts(CancellationToken parent, DateTime deadlineUtc, int stepMs)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        double remaining = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
        double budget = Math.Min(stepMs, Math.Max(1, remaining));
        cts.CancelAfter(TimeSpan.FromMilliseconds(budget));
        return cts;
    }

    static bool BudgetExhausted(ProbeCtx ctx)
        => DateTime.UtcNow.AddMilliseconds(MinUsefulMs) >= ctx.DeadlineUtc;

    static async Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        try { await Task.Delay(RetryBackoffMs * (attempt + 1), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    // Повторяемые ошибки: таймаут, пустой ответ, 5xx, transient-сетевые исключения.
    // НЕ повторяем: 4xx, structural-ошибки парсинга, отмена по внешнему дедлайну.
    static bool IsRetriable(string error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        if (error is "timeout" or "empty_body") return true;
        if (error.StartsWith("http_", StringComparison.Ordinal))
            return int.TryParse(error.AsSpan(5), out int code) && code >= 500;
        return error is "HttpRequestException" or "IOException" or "SocketException" or "HttpIOException";
    }

    static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToArray();
        int mid = s.Length / 2;
        return (s.Length & 1) == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }

    // HTTP-GET с телом-строкой и повтором при timeout/5xx/transient.
    static async Task<(string body, Dictionary<string, string> headers)> SendStringWithRetryAsync(
        string url, Dictionary<string, string> headers, bool jsonAccept, ProbeCtx ctx, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (BudgetExhausted(ctx))
                return (null, headers);

            try
            {
                using var step = StepCts(ct, ctx.DeadlineUtc, ctx.ResolveStepMs);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(req, headers);
                if (jsonAccept)
                    req.Headers.Accept.ParseAdd("application/json,text/plain,*/*");

                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, step.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return (await resp.Content.ReadAsStringAsync(step.Token).ConfigureAwait(false), headers);

                // 4xx — не повторяем; 5xx — повторяем пока есть попытки.
                if ((int)resp.StatusCode < 500 || attempt >= ctx.MaxRetries)
                    return (null, headers);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (null, headers); // внешний дедлайн / клиент ушёл
            }
            catch (OperationCanceledException)
            {
                if (attempt >= ctx.MaxRetries) return (null, headers); // step-timeout исчерпал попытки
            }
            catch when (attempt < ctx.MaxRetries)
            {
                // transient сетевая ошибка — повторяем
            }
            catch
            {
                return (null, headers);
            }

            await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
        }
    }

    #endregion

    static async Task<(string url, Dictionary<string, string> headers, string quality, int qScore)>
        ResolveStreamAsync(string balanserUrl, bool isSerial, Dictionary<string, string> loopbackHeaders, ProbeCtx ctx, CancellationToken ct)
    {
        string url = balanserUrl;
        if (isSerial)
            url += (url.Contains('?') ? "&" : "?") + "s=1&e=1";
        if (!url.Contains("rjson=true"))
            url += (url.Contains('?') ? "&" : "?") + "rjson=true";

        string body = await GetLoopbackJsonAsync(url, loopbackHeaders, ctx, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body))
            return (null, null, null, 0);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("rch", out var rchEl) && rchEl.ValueKind == JsonValueKind.True)
                return (RCH_MARKER, null, null, 0);

            // Standard shape: MovieResponseDto / EpisodeResponseDto / SeasonResponseDto with "data" array.
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                var first = data[0];
                string type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                if (type == "season")
                {
                    if (first.TryGetProperty("url", out var seasonUrl) && seasonUrl.ValueKind == JsonValueKind.String)
                        return await ResolveStreamFromUrlAsync(seasonUrl.GetString(), loopbackHeaders, ctx, ct).ConfigureAwait(false);
                    return (null, null, null, 0);
                }

                // episode / movie — extract directly.
                return ExtractStreamFromMovieDto(first);
            }

            // Flat shape: some balansers (Phantom etc.) return MovieDto directly at the root
            // when called with rjson=true — { title, method, url, quality: {...} }.
            if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("url", out _) || root.TryGetProperty("quality", out _)))
                return ExtractStreamFromMovieDto(root);

            return (null, null, null, 0);
        }
        catch
        {
            return (null, null, null, 0);
        }
    }

    static async Task<(string url, Dictionary<string, string> headers, string quality, int qScore)>
        ResolveStreamFromUrlAsync(string nextUrl, Dictionary<string, string> loopbackHeaders, ProbeCtx ctx, CancellationToken ct)
    {
        if (!nextUrl.Contains("rjson=true"))
            nextUrl += (nextUrl.Contains('?') ? "&" : "?") + "rjson=true";

        string body = await GetLoopbackJsonAsync(nextUrl, loopbackHeaders, ctx, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body))
            return (null, null, null, 0);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("rch", out var rchEl) && rchEl.ValueKind == JsonValueKind.True)
                return (RCH_MARKER, null, null, 0);

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                return ExtractStreamFromMovieDto(data[0]);

            // Flat MovieDto at root.
            if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("url", out _) || root.TryGetProperty("quality", out _)))
                return ExtractStreamFromMovieDto(root);

            return (null, null, null, 0);
        }
        catch
        {
            return (null, null, null, 0);
        }
    }

    // True for URLs like "http://lampac/lite/phantom/video?..." that return
    // ANOTHER JSON (not a stream). Stream URLs end with media extensions
    // (.m3u8/.mp4/.ts/.m4s) or go through /proxy/...
    static bool IsLampacInternalEndpoint(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!url.Contains("/lite/", StringComparison.OrdinalIgnoreCase)) return false;
        if (_streamExt.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    static (string url, Dictionary<string, string> headers, string quality, int qScore)
        ExtractStreamFromMovieDto(JsonElement item)
    {
        Dictionary<string, string> headers = null;
        if (item.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            headers = new Dictionary<string, string>();
            foreach (var p in headersEl.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    headers[p.Name] = p.Value.GetString();
        }

        if (item.TryGetProperty("quality", out var qualityEl) && qualityEl.ValueKind == JsonValueKind.Object)
        {
            var qualities = new List<(string label, int score, string url)>();
            foreach (var p in qualityEl.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                if (p.Name.Equals("auto", StringComparison.OrdinalIgnoreCase)) continue;
                int score = QualityWeight(p.Name);
                if (score > 0)
                    qualities.Add((p.Name, score, CleanUrl(p.Value.GetString())));
            }

            if (qualities.Count > 0)
            {
                // Prefer 1080p (matches what the actual player picks by default).
                // Fall back: 720p → 1440p → 2160p → highest available.
                // We deliberately do NOT pick the max because some balansers
                // return a placeholder/empty stream for 2160p while 1080p works fine.
                var pick = qualities.FirstOrDefault(q => q.score == 60); // 1080p
                if (pick.url == null) pick = qualities.FirstOrDefault(q => q.score == 40); // 720p
                if (pick.url == null) pick = qualities.FirstOrDefault(q => q.score == 70); // 1440p
                if (pick.url == null) pick = qualities.OrderByDescending(q => q.score).First();
                return (pick.url, headers, pick.label, pick.score);
            }
        }

        string maxq = item.TryGetProperty("maxquality", out var mq) && mq.ValueKind == JsonValueKind.String ? mq.GetString() : null;

        // "stream" приоритетнее "url": при method:"call" поле "url" — это call-эндпоинт
        // (/lite/.../video?…), который сам возвращает JSON и уводит probe в internal_loop,
        // а реальный playable-стрим лежит именно в "stream".
        if (item.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(streamEl.GetString()))
        {
            return (CleanUrl(streamEl.GetString()), headers, maxq, QualityWeight(maxq));
        }

        if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            return (CleanUrl(urlEl.GetString()), headers, maxq, QualityWeight(maxq));
        }

        return (null, null, null, 0);
    }

    static int QualityWeight(string label)
    {
        if (string.IsNullOrEmpty(label))
            return 1;
        var m = _qualityRx.Match(label);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int q))
            return 1;
        return q switch
        {
            >= 2160 => 9,
            >= 1440 => 7,
            >= 1080 => 6,
            >= 720 => 4,
            >= 480 => 2,
            _ => 1
        };
    }

    static string CleanUrl(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;
        int sp = raw.IndexOf(' ');
        return (sp > 0 ? raw[..sp] : raw).Trim();
    }

    static bool IsM3u8(string url)
        => url != null && (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || url.Contains("m3u8?", StringComparison.OrdinalIgnoreCase));

    static readonly Regex _resolutionRx = new(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex _bandwidthRx = new(@"BANDWIDTH=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Returns (url, detected_quality_label, isVariantPlaylist).
    // If master m3u8 — picks variant closest to 1080p (prefer 1080 ≥ 720 ≥ 1440 ≥ max).
    // If media m3u8 — returns first segment URL.
    static (string url, string quality, bool isVariantPlaylist) PickBestSegmentOrVariant(string manifest, string manifestUrl)
    {
        var variants = new List<(int height, int bandwidth, string url)>();
        string firstSegment = null;

        using var reader = new StringReader(manifest);
        string line;
        string pendingStreamInf = null;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                pendingStreamInf = line;
                continue;
            }

            if (pendingStreamInf != null && !line.StartsWith('#'))
            {
                int height = 0;
                var rm = _resolutionRx.Match(pendingStreamInf);
                if (rm.Success) int.TryParse(rm.Groups[2].Value, out height);

                int bw = 0;
                var bm = _bandwidthRx.Match(pendingStreamInf);
                if (bm.Success) int.TryParse(bm.Groups[1].Value, out bw);

                variants.Add((height, bw, line));
                pendingStreamInf = null;
                continue;
            }

            if (firstSegment == null && !line.StartsWith('#') && _streamExt.Any(e => line.Contains(e, StringComparison.OrdinalIgnoreCase)))
            {
                firstSegment = line;
            }
        }

        // Media playlist (segments) — return first segment.
        if (variants.Count == 0)
        {
            return firstSegment != null
                ? (ResolveRelative(firstSegment, manifestUrl), null, false)
                : (null, null, false);
        }

        // Master playlist — pick best variant.
        var ordered = variants.OrderByDescending(v => v.height > 0 ? v.height : v.bandwidth / 1000).ToList();
        var pick = ordered.FirstOrDefault(v => v.height >= 720 && v.height <= 1440);
        if (pick.url == null) pick = ordered.FirstOrDefault(v => v.height >= 720);
        if (pick.url == null) pick = ordered.First();

        string qLabel = pick.height > 0 ? $"{pick.height}p" : null;
        return (ResolveRelative(pick.url, manifestUrl), qLabel, true);
    }

    static string ResolveRelative(string maybeRelative, string baseUrl)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out var abs))
            return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), maybeRelative, out var combined))
            return combined.ToString();
        return maybeRelative;
    }

    static async Task<(string content, Dictionary<string, string> headers)>
        GetTextAsync(string url, Dictionary<string, string> headers, ProbeCtx ctx, CancellationToken ct)
        => await SendStringWithRetryAsync(url, headers, jsonAccept: false, ctx, ct).ConfigureAwait(false);

    static async Task<string> GetLoopbackJsonAsync(string url, Dictionary<string, string> headers, ProbeCtx ctx, CancellationToken ct)
    {
        var (body, _) = await SendStringWithRetryAsync(url, headers, jsonAccept: true, ctx, ct).ConfigureAwait(false);
        return body;
    }

    static void ApplyHeaders(HttpRequestMessage req, Dictionary<string, string> headers)
    {
        if (headers == null) return;
        foreach (var kv in headers)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            try
            {
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            catch { }
        }
    }
}
