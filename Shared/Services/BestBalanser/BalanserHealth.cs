namespace Shared.Services.BestBalanser;

public sealed class BalanserHealth
{
    public string plugin { get; init; }

    public string name { get; init; }

    public string url { get; init; }

    public bool isWorking { get; init; }

    public bool isRch { get; init; }

    public string error { get; init; }

    public int qualityScore { get; init; }

    public string quality { get; init; }

    public double ttfbSeconds { get; init; }

    public double throughputBytesPerSec { get; init; }

    public long bytesRead { get; init; }

    public double finalScore =>
        isWorking
            ? (throughputBytesPerSec / 1048576.0) * Math.Max(qualityScore, 1) / (1.0 + ttfbSeconds)
            : 0;
}

public enum BalanserHealthCacheTtl
{
    Success,
    Failure
}
