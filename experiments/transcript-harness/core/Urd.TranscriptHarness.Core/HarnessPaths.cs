namespace Urd.TranscriptHarness.Core;

public static class HarnessPaths
{
    /// <summary>Host executable is expected beside the runner (ProjectReference output).</summary>
    public static string LocateHostExecutable()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Urd.TranscriptHarness.StoreHost.exe");
        if (!File.Exists(exe)) exe = Path.Combine(AppContext.BaseDirectory, "Urd.TranscriptHarness.StoreHost");
        return File.Exists(exe)
            ? exe
            : throw new HarnessError($"Store host executable not found next to runner at {AppContext.BaseDirectory}");
    }
}