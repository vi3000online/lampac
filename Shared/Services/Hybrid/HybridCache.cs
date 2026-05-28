using Microsoft.Extensions.Caching.Memory;

namespace Shared.Services.Hybrid;

public static class HybridCache
{
    static IHybridCache _instance = new HybridFileCache();
    static IMemoryCache _instanceMemory;
    static IDistributedLock _distributedLock;


    public static IHybridCache Get()
        => _instance;

    public static IMemoryCache GetMemory()
        => _instanceMemory;

    public static IDistributedLock GetDistributedLock()
        => _distributedLock;


    public static void Configure(IHybridCache hybridCache)
    {
        if (hybridCache == null)
            return;

        _instance = hybridCache;
    }

    public static void Configure(IMemoryCache mem)
    {
        if (mem == null)
            return;

        _instanceMemory = mem;
    }

    public static void ConfigureDistributedLock(IDistributedLock dl)
    {
        _distributedLock = dl;
    }
}
