namespace Shared.Models.AppConf;

public class HybridCacheConf
{
    public string type { get; set; }

    public bool memExtend { get; set; }

    public int extend { get; set; }

    public PgConf pg { get; set; } = new PgConf();
}

public class PgConf
{
    public string connectionString { get; set; }

    public int statsFlushSeconds { get; set; } = 30;

    public int gcSeconds { get; set; } = 60;

    public int gcGraceMinutes { get; set; } = 60;

    public int advisoryLockTimeoutMs { get; set; } = 25000;

    // Кросс-инстансовый advisory-lock держит соединение PG открытым (idle in transaction)
    // на всё время парсинга апстрима. На холодном кеше это забивает пул и роняет прод.
    // In-process SemaphorManager уже даёт single-flight внутри инстанса — лок по умолчанию выключен.
    public bool distributedLock { get; set; } = false;
}
