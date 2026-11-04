using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Urd.TranscriptHarness.ReferenceStore;

namespace Urd.TranscriptHarness.Runner;

// Shared harness runner. Commands:
//   selftest  — focused correctness checks of generator, oracle, reference
//               store, edit/deletion invariants, streaming, and child-process
//               lifecycle mechanics (no durability claims: reference store is
//               memory-only).
//   smoke     — end-to-end pipeline: generate trace, run store host child,
//               oracle-verify checkpoints, sample named latency stats and
//               child-process resources, write a machine-readable report to
//               the owned OS-temp run directory.
//
// This is smoke-scale by design; large tiers exist as configuration only (see
// experiments/transcript-harness/README.md). Stdout is a convenience summary,
// never the evidence record.

public static class Program
{
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(), "urd-transcript-harness");

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "help")
            {
                PrintHelp();
                return 2;
            }
            return args[0] switch
            {
                "selftest" => SelfTests.RunAll(),
                "smoke" => RunSmoke(args[1..]),
                "validate" => RunValidate(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is HarnessVerificationError ? $"VERIFICATION FAILED: {ex.Message}" : ex.ToString());
            return 1;
        }
    }

    private static int RunSmoke(string[] args)
    {
        var scale = ScaleConfigFromArgs(args);
        var storeAssembly = Arg(args, "--store-assembly");
        var storeType = Arg(args, "--store-type") ?? "Urd.TranscriptHarness.ReferenceStore.ReferenceTranscriptStore";
        var tempRoot = Arg(args, "--temp-root") ?? Path.Combine(TempRoot, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}");

        Directory.CreateDirectory(tempRoot);
        var storeRoot = Path.Combine(tempRoot, "store");
        var attachmentRoot = PrepareAttachments(tempRoot, scale);
        var tracePath = Path.Combine(tempRoot, "trace.jsonl");

        var generated = TraceGenerator.Generate(scale);
        WriteTraceAudit(tracePath, generated, scale);

        var warnings = new List<string>();
        var launch = new IpcStoreAdapter.StoreHostLaunch(
            HarnessPaths.LocateHostExecutable(), storeAssembly, storeType, storeRoot, attachmentRoot);

        var stopwatch = Stopwatch.StartNew();
        using var adapter = IpcStoreAdapter.Spawn(launch);
        var sampler = new ChildResourceSampler(adapter.Process);
        var descriptor = adapter.Describe();
        if (descriptor.AckTier == AckTier.MemoryOnly)
        {
            warnings.Add("Reference/memory-only store: run proves harness mechanics, NOT crash or durability behaviour");
        }

        var model = new OracleModel();
        var executor = new TraceExecutor(adapter, model);

        // "Before" statistics are a spawn-time sample, not a post-run re-read.
        var statsBefore = adapter.QueryStatistics();

        executor.Replay(generated.Ops);

        // Trace-workload resource peak: read/reset BEFORE oracle verification so
        // comparison reads never pollute the engine's workload figure
        // (STORAGE-EXPERIMENT.md measurement rules).
        var resources = sampler.ReadAndReset();

        // Final full verification (streamed content, full logical equality);
        // sampled separately as verification cost.
        Oracle.VerifyAll(adapter, model);
        var verifyResources = sampler.ReadAndReset();

        var statsAfter = adapter.QueryStatistics();
        sampler.Dispose();
        adapter.Close();
        stopwatch.Stop();

        if (generated.Attachments.Count == 0) warnings.Add("No attachment fixtures were generated");
        warnings.Add("replay resource peaks include interleaved oracle checkpoint verification; verification sampled separately (see README)");
        foreach (var attachment in generated.Attachments)
        {
            var path = Path.Combine(attachmentRoot, attachment.RelativePath);
            if (!File.Exists(path))
            {
                throw new HarnessVerificationError($"Attachment fixture missing: {path}");
            }
        }

        var footprint = DiskFootprint.Measure(storeRoot);
        var summary = new RunSummary(
            descriptor.StoreName,
            descriptor,
            generated.Ops.Count,
            executor.ReadLatency,
            executor.MutationLatency,
            new Dictionary<string, NamedStats>(adapter.HostLatency),
            new Dictionary<string, NamedStats>(adapter.HostAllocationBytes),
            executor.Checkpoints,
            statsBefore?.ManagedAllocatedBytes,
            statsAfter?.ManagedAllocatedBytes,
            resources.Length > 0 ? resources.Max(s => s.PrivateBytes) : 0,
            resources.Length > 0 ? resources.Max(s => s.WorkingSetBytes) : 0,
            verifyResources.Length > 0 ? verifyResources.Max(s => s.PrivateBytes) : 0,
            verifyResources.Length > 0 ? verifyResources.Max(s => s.WorkingSetBytes) : 0,
            footprint.TotalBytes,
            footprint.FileCount,
            WarningsList(warnings, scale)
        );
        var reportPath = ReportWriter.Write(tempRoot, "report.json", summary);
        Console.WriteLine($"smoke ok: ops={summary.OpCount} checkpoints={summary.Checkpoints} wall={stopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine($"report: {reportPath}");
        return 0;
    }

    private static string[] WarningsList(List<string> warnings, ScaleConfig scale) =>
        [.. warnings, scale.Notes];

    private static string PrepareAttachments(string tempRoot, ScaleConfig scale)
    {
        var attachmentRoot = Path.Combine(tempRoot, "attachments");
        Directory.CreateDirectory(attachmentRoot);
        // Deterministic synthetic bytes; images are files with references only,
        // created by the harness for every store identically.
        foreach (var attachment in attachmentPlan(scale))
        {
            var path = Path.Combine(attachmentRoot, attachment.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, FixtureBytes(attachment.ImageId, attachment.ByteCount));
        }
        return attachmentRoot;
    }

    private static List<AttachmentFixture> attachmentPlan(ScaleConfig scale) =>
        [.. Enumerable.Range(0, scale.ImageFileCount)
            .Select(i => new AttachmentFixture($"img-{i}", $"{i % 3}/img-{i}.bin", 1024 * (1 + (i % 4))))];

    private static byte[] FixtureBytes(string imageId, int bytes)
    {
        var data = new byte[bytes];
        new Random(imageId.Length * 31 + bytes).NextBytes(data);
        return data;
    }

    private static ScaleConfig ScaleConfigFromArgs(string[] args)
    {
        var scale = new ScaleConfig();
        if (TryInt(args, "--seed", out var seed)) scale = scale with { Seed = seed };
        if (TryInt(args, "--sessions", out var sessions)) scale = scale with { SessionCount = sessions };
        if (TryInt(args, "--messages-min", out var minMsgs)) scale = scale with { MinMessages = minMsgs };
        if (TryInt(args, "--messages-max", out var maxMsgs)) scale = scale with { MaxMessages = maxMsgs };
        if (TryInt(args, "--medium-kb", out var mediumKb)) scale = scale with { MediumResultKB = mediumKb };
        if (TryInt(args, "--medium-count", out var mediumCount)) scale = scale with { MediumResultCount = mediumCount };
        if (TryInt(args, "--large-kb", out var largeKb)) scale = scale with { LargeResultKB = largeKb };
        return scale;
    }

    private static void WriteTraceAudit(string path, GeneratedTrace trace, ScaleConfig scale)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#scale {JsonSerializer.Serialize(scale, HarnessJson.Options)}");
        foreach (var op in trace.Ops) sb.AppendLine(TraceAudit.ToLine(op));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }


    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private static bool TryInt(string[] args, string name, out int value)
    {
        value = 0;
        var text = Arg(args, name);
        return text is not null && int.TryParse(text, out value);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>Persistence validation: clean reopen + kill-with-inflight reopen, content checked.</summary>
    private static int RunValidate(string[] args)
    {
        var messages = 12;
        if (TryInt(args, "--messages", out var value)) messages = value;
        var storeAssembly = Arg(args, "--store-assembly");
        var storeType = Arg(args, "--store-type") ?? "Urd.TranscriptHarness.ReferenceStore.ReferenceTranscriptStore";
        var tempRoot = Arg(args, "--temp-root") ?? Path.Combine(TempRoot, $"validate-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}");
        Directory.CreateDirectory(tempRoot);

        var launch = new IpcStoreAdapter.StoreHostLaunch(
            HarnessPaths.LocateHostExecutable(),
            storeAssembly is null ? null : Path.GetFullPath(storeAssembly),
            storeType,
            Path.Combine(tempRoot, "store"),
            Path.Combine(tempRoot, "attachments"));

        var outcome = ValidateRunner.Run(launch, messages);
        var reportPath = ReportWriter.Write(tempRoot, "validate.json", outcome);
        Console.WriteLine(
            $"validate [{outcome.Tier}]: clean={outcome.CleanReopenVerified} kill={outcome.KillReopenVerified}" +
            (outcome.KillResolution is { } r ? $" ({r})" : string.Empty));
        foreach (var note in outcome.Notes) Console.WriteLine($"note: {note}");
        Console.WriteLine($"report: {reportPath}");

        if (!outcome.DurableJudgementPossible)
        {
            Console.WriteLine("memory-only adapter: persistence verdicts suppressed; mechanics only");
            return 0;
        }
        return outcome.CleanReopenVerified && outcome.KillReopenVerified ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("usage: Urd.TranscriptHarness.Runner <selftest|smoke|validate> [options]");
        Console.WriteLine("  smoke [--seed n] [--sessions n] [--messages-min n] [--messages-max n]");
        Console.WriteLine("        [--medium-kb n] [--medium-count n] [--large-kb n]");
        Console.WriteLine("        [--store-assembly <dll>] [--store-type <FullTypeName>] [--temp-root <dir>]");
        Console.WriteLine("  validate [--messages n] [--store-assembly <dll>] [--store-type <FullTypeName>] [--temp-root <dir>]");
        Console.WriteLine("        clean-reopen and kill-inflight persistence checks (MemoryOnly: mechanics only)");
        Console.WriteLine("Large tiers are configuration-only in this milestone; nothing huge is generated by default.");
    }
}

/// <summary>One-line audit JSON per trace op (machine-checkable, written to OS temp only).</summary>
public static class TraceAudit
{
    public static string ToLine(TraceOp op) => op switch
    {
        TraceAppend a => Wrap("append", w =>
        {
            w.WriteString("sessionId", a.SessionId);
            w.WritePropertyName("message");
            HarnessJson.WriteMessage(w, a.Message);
        }),
        TraceStreamDraft d => Wrap("streamDraft", w =>
        {
            w.WriteString("sessionId", d.SessionId);
            w.WriteString("streamId", d.StreamId);
            w.WriteString("chunk", d.Chunk);
        }),
        TraceFinalize f => Wrap("finalize", w =>
        {
            w.WriteString("sessionId", f.SessionId);
            w.WriteString("streamId", f.StreamId);
            w.WritePropertyName("message");
            HarnessJson.WriteMessage(w, f.FinalMessage);
        }),
        TraceReplaceSuffix r => Wrap("replaceSuffix", w =>
        {
            w.WriteString("sessionId", r.SessionId);
            w.WriteNumber("from", r.FromOrdinal);
            w.WriteStartArray("messages");
            foreach (var message in r.NewMessages) HarnessJson.WriteMessage(w, message);
            w.WriteEndArray();
        }),
        TraceDeleteSession d => Wrap("deleteSession", w =>
        {
            w.WriteString("sessionId", d.SessionId);
            w.WriteString("policy", d.Policy.ToString());
        }),
        TraceCreateAssignment c => Wrap("createAssignment", w =>
        {
            w.WriteString("assignmentId", c.AssignmentId);
            w.WriteString("targetSessionId", c.TargetSessionId);
            w.WriteString("title", c.Title);
        }),
        TraceSettle s => Wrap("settle", w =>
        {
            w.WriteString("assignmentId", s.AssignmentId);
            w.WriteString("resultText", s.ResultText);
        }),
        TraceListSessions => Wrap("listSessions", static _ => { }),
        TraceOpenRecent o => Wrap("openRecent", w =>
        {
            w.WriteString("sessionId", o.SessionId);
            w.WriteNumber("count", o.Count);
        }),
        TracePageOlder o => Wrap("pageOlder", w =>
        {
            w.WriteString("sessionId", o.SessionId);
            w.WriteNumber("before", o.Before);
            w.WriteNumber("count", o.Count);
        }),
        TraceReadRange o => Wrap("readRange", w =>
        {
            w.WriteString("sessionId", o.SessionId);
            w.WriteNumber("from", o.From);
            w.WriteNumber("to", o.To);
        }),
        TraceWindowPreview o => Wrap("windowPreview", w =>
        {
            w.WriteString("sessionId", o.SessionId);
            w.WriteNumber("count", o.Count);
            w.WriteNumber("previewChars", o.PreviewChars);
        }),
        TraceFetchToolResult o => Wrap("fetchToolResult", w =>
        {
            w.WriteString("sessionId", o.SessionId);
            w.WriteNumber("ordinal", o.Ordinal);
            w.WriteNumber("partIndex", o.PartIndex);
        }),
        TraceCheckpoint => Wrap("checkpoint", static _ => { }),
        TraceCrashMarker c => Wrap("crashMarker", w => w.WriteString("note", c.Note)),
        _ => throw new HarnessError($"Unknown trace op {op.GetType().Name}"),
    };

    private static string Wrap(string name, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("op", name);
            writer.WriteStartObject("p");
            body(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}