using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Urd.TranscriptHarness.Core;

// Measurement utilities. Named statistics only (min/p10/median/p90/max/N, linear
// interpolation where sample counts allow) — no percentiles claimed beyond
// sample support, no p99, no aggregate score.

public sealed record NamedStats(double Min, double P10, double Median, double P90, double Max, int Count)
{
    public override string ToString() =>
        $"min {Min:0.###}ms, med {Median:0.###}ms, p90 {P90:0.###}ms, max {Max:0.###}ms (n={Count})";
}

public static class Stats
{
    public static NamedStats Summarize(IReadOnlyList<double> samples)
    {
        var sorted = samples.OrderBy(s => s).ToArray();
        double At(double percentile)
        {
            if (sorted.Length == 0) return 0;
            if (sorted.Length == 1) return sorted[0];
            var index = percentile / 100.0 * (sorted.Length - 1);
            var lower = (int)Math.Floor(index);
            var upper = (int)Math.Ceiling(index);
            var fraction = index - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
        }
        return new NamedStats(sorted[0], At(10), At(50), At(90), sorted[^1], sorted.Length);
    }
}

/// <summary>
/// Separate resource figures for the store-host child process: private bytes
/// and working set (never merged, never treated as one number), sampled on a
/// timer. Managed allocations are reported only through a store's own
/// IStoreStatistics; they are excluded here. Virtual/mmap reservation is not
/// sampled at all — reservation is not a cache budget and is recorded by the
/// adapter, not measured as memory.
/// </summary>
public sealed class ChildResourceSampler : IDisposable
{
    public sealed record Sample(long TimestampMs, long PrivateBytes, long WorkingSetBytes);

    private readonly Process _process;
    private readonly Timer _timer;
    private readonly List<Sample> _samplesSinceLastRead = [];
    private readonly object _lock = new();

    public ChildResourceSampler(Process process) : this(process, TimeSpan.FromMilliseconds(100))
    {
    }

    public ChildResourceSampler(Process process, TimeSpan interval)
    {
        _process = process;
        _timer = new Timer(_ =>
        {
            try
            {
                if (process.HasExited) return;
                process.Refresh();
                lock (_lock)
                {
                    _samplesSinceLastRead.Add(new Sample(
                        DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond,
                        process.PrivateMemorySize64,
                        process.WorkingSet64));
                }
            }
            catch
            {
                // Exited mid-sample: sampling stops; already-recorded samples remain valid.
            }
        }, null, interval, interval);
    }

    public Sample[] ReadAndReset()
    {
        lock (_lock)
        {
            var result = _samplesSinceLastRead.ToArray();
            _samplesSinceLastRead.Clear();
            return result;
        }
    }

    public (long PrivateBytes, long WorkingSetBytes) Peak()
    {
        lock (_lock)
        {
            return (_samplesSinceLastRead.Count > 0 ? _samplesSinceLastRead.Max(s => s.PrivateBytes) : 0,
                    _samplesSinceLastRead.Count > 0 ? _samplesSinceLastRead.Max(s => s.WorkingSetBytes) : 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Writes the machine-readable run report into the owned OS-temp run directory.</summary>
public static class ReportWriter
{
    public static string Write(string runDir, string fileName, object report)
    {
        Directory.CreateDirectory(runDir);
        var path = Path.Combine(runDir, fileName);
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, SkipValidation = false });
        JsonSerializer.Serialize(writer, report, HarnessJson.Options);
        return path;
    }
}

public static class DiskFootprint
{
    public static (long TotalBytes, int FileCount, string FilesNote) Measure(string directory)
    {
        if (!Directory.Exists(directory)) return (0, 0, "absent");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        return (files.Sum(f => new FileInfo(f).Length), files.Length,
            string.Join(", ", files.Select(Path.GetFileName).OrderBy(f => f)));
    }
}