using Shared.Models.Module;

namespace Shared.Models.AppConf;

public class OnlineConf : ModuleBaseConf
{
    public string name { get; set; }

    public bool version { get; set; }

    public bool checkOnlineSearch { get; set; }

    public bool btn_priority_forced { get; set; }

    public HashSet<string> with_search { get; set; }

    public BestBalanserConf bestBalanser { get; set; } = new BestBalanserConf();
}

public class BestBalanserConf
{
    public bool enable { get; set; }

    public bool hideBroken { get; set; } = true;

    public int totalTimeoutMs { get; set; } = 7000;

    public int perProbeTimeoutMs { get; set; } = 5000;

    public int successCacheMinutes { get; set; } = 30;

    public int failureCacheMinutes { get; set; } = 3;

    // Сколько независимых замеров скорости делать на один источник.
    // Итоговый throughput/ttfb — медиана по успешным замерам.
    public int speedSamples { get; set; } = 3;

    // Сколько повторных попыток при таймауте / transient-ошибке
    // (на каждый сетевой шаг: резолв стрима, чтение манифеста, замер).
    public int maxRetries { get; set; } = 2;
}
