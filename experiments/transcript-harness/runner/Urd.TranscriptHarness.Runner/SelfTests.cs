using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Urd.TranscriptHarness.ReferenceStore;

namespace Urd.TranscriptHarness.Runner;

// Focused self-tests for the shared runner: generator determinism, oracle
// detection of ordering/content/deletion faults, the reference adapter's
// contract behaviour, streaming/finalize rules, suffix-edit renumbering and
// truncation, assignment/deletion invariants, and child-process lifecycle
// mechanics. Nothing here claims durability: the reference store is
// memory-only, so kill tests validate harness mechanics only.

public static class SelfTests
{
    public static int RunAll()
    {
        var failures = new List<string>();
        void Check(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine($"ok   {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Check("trace generator is deterministic for a fixed seed", GeneratorDeterminism);
        Check("oracle detects reversed window order", () => FaultDetected(f => f.ReverseWindows = true));
        Check("oracle detects altered message content", () => FaultDetected(f => f.AlterText = true));
        Check("oracle detects wrong session counts", () => FaultDetected(f => f.WrongCounts = true));
        Check("oracle detects dropped records", () => FaultDetected(f => f.DropLastRecord = true));
        Check("oracle detects wrong tool-result content", () => FaultDetected(f => f.AlterToolResult = true));
        Check("oracle detects deleted session not reflected in model", DeletionFaultDetected);
        Check("full catalog round trip on reference store (in-process)", ReferenceRoundTrip);
        Check("streaming rules on reference store", StreamingRules);
        Check("preview window rules (metadata-only, bounded)", PreviewWindowRules);
        Check("suffix edit renumbering and truncation", SuffixEditRules);
        Check("delete/assignment invariants", DeleteInvariants);
        Check("child lifecycle: ready/ack/read/close", HostLifecycle);
        Check("host method timing and allocation recorded separately", HostMetricSeparation);
        Check("plugin loads via resolver with external dependency", PluginResolverLoad);
        Check("validate runner mechanics (memory-only path)", ValidateMechanics);
        Check("kill mechanics (mechanics only; no durability interpretation)", KillMechanics);

        if (failures.Count > 0)
        {
            Console.WriteLine($"selftests: {failures.Count} failed");
            return 1;
        }
        Console.WriteLine("selftests: all passed");
        return 0;
    }

    private static void GeneratorDeterminism()
    {
        var scale = new ScaleConfig { Seed = 11, SessionCount = 3, MinMessages = 2, MaxMessages = 6 };
        var first = TraceGenerator.Generate(scale);
        var second = TraceGenerator.Generate(scale);
        if (first.Ops.Count != second.Ops.Count) throw new HarnessError("op count differs for identical seeds");
        foreach (var (a, b) in first.Ops.Zip(second.Ops))
        {
            if (!string.Equals(TraceAudit.ToLine(a), TraceAudit.ToLine(b), StringComparison.Ordinal))
            {
                throw new HarnessError("identical seeds produced differing ops");
            }
        }
        var varied = TraceGenerator.Generate(scale with { Seed = 12 });
        if (varied.Ops.Zip(first.Ops).All(p => TraceAudit.ToLine(p.First) == TraceAudit.ToLine(p.Second)))
        {
            throw new HarnessError("different seed produced an identical trace");
        }
    }

    // Replay a small trace cleanly against the reference store, then verify
    // through a deliberately faulted read surface: the oracle must reject it.
    private static void FaultDetection(Action<FaultFlags> enable)
    {
        var scale = new ScaleConfig { Seed = 5, SessionCount = 2, MinMessages = 4, MaxMessages = 8 };
        var trace = TraceGenerator.Generate(scale);
        // Remove deletes so post-trace verification has live sessions to inspect;
        // both sides stay consistent because the model ops are filtered identically.
        var ops = trace.Ops.Where(o => o is not TraceDeleteSession).ToArray();
        var model = new OracleModel();
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        new TraceExecutor(store, model).Replay(ops);
        Oracle.VerifyAll(store, model); // sanity: clean store verifies

        var flags = new FaultFlags();
        enable(flags);
        using var faulted = new FaultyClient(store, flags);
        try
        {
            Oracle.VerifyAll(faulted, model);
            throw new HarnessError("faulted store passed verification");
        }
        catch (HarnessVerificationError)
        {
            // Expected: the injected fault was detected.
        }
    }

    private static void FaultDetected(Action<FaultFlags> enable) => FaultDetection(enable);

    // A store that silently drops a deleted-then-recreated history (i.e. loses
    // records entirely) would look like this client: sessions missing entirely.
    private static void DeletionFaultDetected()
    {
        var scale = new ScaleConfig { Seed = 5, SessionCount = 2, MinMessages = 4, MaxMessages = 8 };
        var trace = TraceGenerator.Generate(scale);
        var ops = trace.Ops.Where(o => o is not TraceDeleteSession).ToArray();
        var model = new OracleModel();
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        new TraceExecutor(store, model).Replay(ops);
        using var amnesiac = new AmnesiacClient(store);
        try
        {
            Oracle.VerifyAll(amnesiac, model);
            throw new HarnessError("store missing entire sessions passed verification");
        }
        catch (HarnessVerificationError)
        {
            // Expected.
        }
    }

    private static void ReferenceRoundTrip()
    {
        var scale = new ScaleConfig { Seed = 13, SessionCount = 4, MinMessages = 6, MaxMessages = 10 };
        var trace = TraceGenerator.Generate(scale);
        var model = new OracleModel();
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        var executor = new TraceExecutor(store, model);
        executor.Replay(trace.Ops);
        Oracle.VerifyAll(store, model);
        if (executor.Checkpoints == 0) throw new HarnessError("no checkpoints ran");
    }

    private static void StreamingRules()
    {
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        var message = MessageOf("assistant", "drafted body");
        try
        {
            store.FinalizeStream("s", "unknown", message);
            throw new HarnessError("finalize of unknown stream was accepted");
        }
        catch (InvalidOperationException) { /* expected */ }

        // Batch semantics: successive StreamDraft calls on the SAME stream are
        // incremental persisted batches; a different streamId while one is
        // active is an error.
        store.StreamDraft("s", "draft-1", "one");
        store.StreamDraft("s", "draft-1", "two");
        try
        {
            store.StreamDraft("s", "draft-2", "other");
            throw new HarnessError("conflicting draft stream accepted");
        }
        catch (InvalidOperationException) { /* expected */ }

        var before = store.OpenRecent("s", 10);
        if (before.Length != 0) throw new HarnessError("unfinalized draft visible as a message");
        store.FinalizeStream("s", "draft-1", message);
        var after = store.OpenRecent("s", 10);
        if (after.Length != 1) throw new HarnessError($"finalized stream message count {after.Length} != 1");
    }

    private static void PreviewWindowRules()
    {
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        var longBody = new string('x', 4096);
        store.AppendMessage("s", MessageOf("user", "short question"));
        store.AppendMessage("s", new MessageRecord("assistant",
        [
            new TextPart("thinking aloud"),
            new ToolResultPart("call-1", longBody),
        ]));

        var preview = store.OpenRecentPreview("s", 5, 24);
        if (preview.FromOrdinal != 0 || preview.Items.Count != 2)
        {
            throw new HarnessError($"preview window shape {preview.FromOrdinal}/{preview.Items.Count} != 0/2");
        }
        var parts = preview.Items[1].Parts;
        var toolResult = parts.First(p => p.Kind == "toolResult");
        if (toolResult.ContentChars != longBody.Length)
        {
            throw new HarnessError($"preview content chars {toolResult.ContentChars} != {longBody.Length}");
        }
        if (toolResult.Preview.Length > 24 || toolResult.Preview.Length != 24)
        {
            throw new HarnessError($"preview text length {toolResult.Preview.Length} not capped at 24");
        }
        if (parts.Any(p => p.Preview.Length > 24) || parts.Any(p => p.Preview.Length == longBody.Length))
        {
            throw new HarnessError("preview returned full payload content");
        }
        // Full content remains available through the dedicated fetch path.
        var fetched = store.FetchToolResult("s", 1, 1);
        if (fetched != longBody) throw new HarnessError("FetchToolResult content mismatch");
    }

    private static void SuffixEditRules()
    {
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        var model = new OracleModel();
        var executor = new TraceExecutor(store, model);
        for (var i = 0; i < 6; i++)
        {
            executor.Execute(new TraceAppend("s", MessageOf("user", $"original-{i}")), i);
        }
        var edit = new List<MessageRecord> { MessageOf("user", "edited") };
        var count = store.ReplaceSuffix("s", 2, edit);
        var expected = ModelOps.ReplaceSuffix(model, "s", 2, edit);
        if (count != expected) throw new HarnessError($"replaceSuffix count {count} != expected {expected}");
        var read = store.ReadRange("s", 0, count);
        if (read[0].Parts.OfType<TextPart>().First().Content != "original-0" ||
            read[1].Parts.OfType<TextPart>().First().Content != "original-1")
        {
            throw new HarnessError("prefix messages disturbed by suffix edit");
        }
        if (read[2].Parts.OfType<TextPart>().First().Content != "edited")
        {
            throw new HarnessError("replacement message not at edit point");
        }
        // A shorter replacement truncates the continuation (edit deletes it).
        count = store.ReplaceSuffix("s", 1, new List<MessageRecord>());
        var expectedTruncated = ModelOps.ReplaceSuffix(model, "s", 1, new List<MessageRecord>());
        if (count != 1 || expectedTruncated != 1)
            throw new HarnessError($"truncating replacement count {count}/{expectedTruncated} != 1");
        Oracle.VerifyAll(store, model);
    }

    private static void DeleteInvariants()
    {
        using var store = new ReferenceTranscriptStore();
        store.Open("unused-root", "unused-attachments");
        var model = new OracleModel();
        var executor = new TraceExecutor(store, model);
        executor.Execute(new TraceAppend("s", MessageOf("user", "hello")), 0);
        executor.Execute(new TraceCreateAssignment("a1", "s", "open work"), 1);

        try
        {
            executor.Execute(new TraceDeleteSession("s", DeletePolicy.RequireSettled), 2);
            throw new HarnessError("RequireSettled deletion with an open assignment was accepted");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HarnessVerificationError)
        {
            // Expected: both store and oracle must reject the dangling state.
        }

        executor.Execute(new TraceDeleteSession("s", DeletePolicy.CancelOpen), 3);
        var assignment = store.GetAssignment("a1");
        if (assignment.Status != AssignmentStatus.Cancelled)
        {
            throw new HarnessError($"CancelOpen left assignment {assignment.Status}, expected Cancelled");
        }
        Oracle.VerifyAll(store, model);
    }

    private static void HostLifecycle()
    {
        var tempRoot = CreateSelfTestDir("lifecycle");
        using var adapter = SpawnReferenceHost(tempRoot);
        adapter.AppendMessage("s", MessageOf("user", "via protocol"));
        var readBack = adapter.OpenRecent("s", 10);
        if (readBack.Length != 1 || MessageText(readBack[0]) != "via protocol")
        {
            throw new HarnessError("protocol round trip mismatch");
        }
        if (!adapter.HostLatency.ContainsKey("append") || !adapter.HostLatency.ContainsKey("openRecent"))
        {
            throw new HarnessError("host method latency was not recorded separately from RPC");
        }
        if (!adapter.HostAllocationBytes.ContainsKey("append"))
        {
            throw new HarnessError("host allocation deltas were not recorded");
        }
        adapter.Close();
        adapter.Process.WaitForExit();
        if (adapter.Process.ExitCode != 0)
        {
            throw new HarnessError($"clean close ended with exit code {adapter.Process.ExitCode}");
        }
    }

    private static void HostMetricSeparation()
    {
        var tempRoot = CreateSelfTestDir("metrics");
        using var adapter = SpawnReferenceHost(tempRoot);
        var message = MessageOf("user", "timed op");
        adapter.AppendMessage("s", message);
        adapter.OpenRecent("s", 10);
        adapter.AppendMessage("s", MessageOf("assistant", "timed op 2"));

        var hostLatency = adapter.HostLatency["append"];
        if (hostLatency.Count < 2) throw new HarnessError("host latency samples missing");
        if (hostLatency.Min < 0 || hostLatency.Max > 1000)
        {
            throw new HarnessError($"host append latency implausible: {hostLatency.Min}..{hostLatency.Max}ms");
        }
        var alloc = adapter.HostAllocationBytes["append"];
        if (alloc.Count < 2 || alloc.Min <= 0)
        {
            throw new HarnessError("host append allocation delta missing or non-positive");
        }
        adapter.Close();
    }

    private static void PluginResolverLoad()
    {
        var pluginPath = LocatePluginAssembly();
        var tempRoot = CreateSelfTestDir("plugin");
        using var adapter = IpcStoreAdapter.Spawn(GetPluginLaunch(tempRoot, pluginPath));
        var descriptor = adapter.Describe();
        if (!descriptor.StoreName.Contains("TestPlugin") || !descriptor.StoreName.Contains("Newtonsoft"))
        {
            throw new HarnessError($"plugin descriptor unexpected: {descriptor.StoreName}");
        }
        adapter.AppendMessage("s", MessageOf("user", "through plugin"));
        adapter.AppendMessage("s", MessageOf("assistant", "second record"));
        var read = adapter.ReadRange("s", 0, 2);
        if (read.Length != 2 || MessageText(read[0]) != "through plugin")
        {
            throw new HarnessError("plugin content round trip failed");
        }
        if (!adapter.HostLatency.ContainsKey("append") || !adapter.HostLatency.ContainsKey("readRange"))
        {
            throw new HarnessError("plugin host did not report per-op host-method latency");
        }
        // Plugin does not implement IStoreStatistics, so no allocation channel exists —
        // latency must still arrive.
        adapter.Close();
        adapter.Process.WaitForExit();
        if (adapter.Process.ExitCode != 0)
        {
            throw new HarnessError($"plugin close ended with exit code {adapter.Process.ExitCode}");
        }
    }

    private static void ValidateMechanics()
    {
        // Memory-only plugin: full persistence verdicts must be suppressed;
        // the mechanics path (spawn, acked workload, close/reopen/kill/reopen)
        // must still run without errors and without content claims.
        var pluginPath = LocatePluginAssembly();
        var tempRoot = CreateSelfTestDir("validate");
        var launch = GetPluginLaunch(Path.Combine(tempRoot, "store"), pluginPath);
        var outcome = ValidateRunner.Run(launch, 9);
        if (outcome.Tier != AckTier.MemoryOnly) throw new HarnessError("plugin tier unexpected");
        if (outcome.DurableJudgementPossible) throw new HarnessError("memory-only adapter claimed durability judgement");
        if (outcome.CleanReopenVerified || outcome.KillReopenVerified)
        {
            throw new HarnessError("memory-only validate claimed persistence verification");
        }
    }

    private static string LocatePluginAssembly()
    {
        var dllName = "Urd.TranscriptHarness.Plugin.TestPlugin.dll";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "testplugin", "Urd.TranscriptHarness.Plugin.TestPlugin", "bin",
                "Release", "net10.0", dllName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new HarnessError($"test plugin assembly not found; build testplugin first ({dllName})");
    }

    private static IpcStoreAdapter.StoreHostLaunch GetPluginLaunch(string storeRoot, string pluginPath) =>
        new(
            HarnessPaths.LocateHostExecutable(),
            pluginPath,
            "Urd.TranscriptHarness.Plugin.TestPlugin.NewtonsoftInMemoryStore",
            storeRoot,
            storeRoot + "-attachments");

    private static void KillMechanics()
    {
        var tempRoot = CreateSelfTestDir("kill");
        var adapter = SpawnReferenceHost(tempRoot);
        adapter.AppendMessage("s", MessageOf("user", "acked before kill"));
        // Send a request without awaiting its response, then kill the host.
        adapter.SendRaw(
            "{\"i\":9001,\"op\":\"append\",\"p\":{\"sessionId\":\"s\",\"message\":" +
            "{\"role\":\"user\",\"parts\":[{\"kind\":\"text\",\"content\":\"in flight at kill\"}]}}}");
        adapter.KillNow();
        adapter.Process.WaitForExit();
        if (!adapter.Process.HasExited)
        {
            throw new HarnessError("killed store host still running");
        }
        adapter.Dispose();

        // Reopen: the memory-only reference store starts empty. This asserts
        // harness mechanics only; no durability conclusion is permitted from
        // it (kill acceptance by content comparison activates with a real
        // persistent adapter — see README deferred items).
        using var reopened = SpawnReferenceHost(tempRoot);
        if (reopened.ListSessionsMeta().Length != 0)
        {
            throw new HarnessError("reopened memory-only store unexpectedly retained sessions");
        }
        reopened.Close();
    }

    // ---- helpers ----

    private static string CreateSelfTestDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "urd-transcript-harness", $"selftest-{label}-{Guid.NewGuid().ToString()[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static IpcStoreAdapter SpawnReferenceHost(string tempRoot) =>
        IpcStoreAdapter.Spawn(new IpcStoreAdapter.StoreHostLaunch(
            HarnessPaths.LocateHostExecutable(),
            null,
            "Urd.TranscriptHarness.ReferenceStore.ReferenceTranscriptStore",
            Path.Combine(tempRoot, "store"),
            Path.Combine(tempRoot, "attachments")));

    private static MessageRecord MessageOf(string role, string text) => new(role, [new TextPart(text)]);

    private static string MessageText(MessageRecord message) => message.Parts.OfType<TextPart>().Single().Content;
}

internal sealed class FaultFlags
{
    public bool ReverseWindows;
    public bool AlterText;
    public bool WrongCounts;
    public bool DropLastRecord;
    public bool AlterToolResult;
}

/// <summary>A wrapper producing deliberate logical faults, to confirm the oracle catches them.</summary>
internal sealed class FaultyClient(ITranscriptStore inner, FaultFlags faults) : ITranscriptStore
{
    public StoreDescriptor Describe() => inner.Describe();

    public void Open(string storeRoot, string attachmentRoot) => inner.Open(storeRoot, attachmentRoot);

    public SessionMeta[] ListSessionsMeta()
    {
        var metas = inner.ListSessionsMeta();
        return faults.WrongCounts
            ? [.. metas.Select(m => m with { MessageCount = m.MessageCount + 1 })]
            : metas;
    }

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        var window = inner.OpenRecent(sessionId, count);
        return Transform(window, faults);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        var window = inner.PageOlder(sessionId, beforeOrdinal, count);
        return Transform(window, faults);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        var window = inner.ReadRange(sessionId, fromOrdinal, toOrdinalExclusive);
        return Transform(window, faults);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars) =>
        inner.OpenRecentPreview(sessionId, count, previewChars);

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var content = inner.FetchToolResult(sessionId, ordinal, partIndex);
        return faults.AlterToolResult ? content + "!" : content;
    }

    public long AppendMessage(string sessionId, MessageRecord message) => inner.AppendMessage(sessionId, message);
    public void StreamDraft(string sessionId, string streamId, string chunk) => inner.StreamDraft(sessionId, streamId, chunk);
    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage) => inner.FinalizeStream(sessionId, streamId, finalMessage);
    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages) => inner.ReplaceSuffix(sessionId, fromOrdinal, newMessages);
    public void CreateAssignment(AssignmentRecord assignment) => inner.CreateAssignment(assignment);
    public void SettleAssignment(SettleAssignmentRequest request) => inner.SettleAssignment(request);
    public AssignmentRecord GetAssignment(string assignmentId) => inner.GetAssignment(assignmentId);
    public void DeleteSession(string sessionId, DeletePolicy policy) => inner.DeleteSession(sessionId, policy);

    private static MessageRecord[] Transform(MessageRecord[] window, FaultFlags flags)
    {
        var list = window.ToList();
        if (flags.DropLastRecord && list.Count > 0) list.RemoveAt(list.Count - 1);
        if (flags.ReverseWindows && list.Count > 1) list.Reverse();
        if (flags.AlterText && list.Count > 0)
        {
            list[0] = list[0] with { Parts = [.. list[0].Parts.Select(p => p switch
            {
                TextPart t => new TextPart(t.Content + "corrupted"),
                ToolResultPart r => r with { Content = r.Content + "corrupted" },
                _ => p,
            })] };
        }
        return [.. list];
    }

    public void Dispose()
    {
        // The inner store's lifetime is owned by the self-test wrapper, not here.
    }
}

/// <summary>Simulates a store that lost every session while still reporting "ok".</summary>
internal sealed class AmnesiacClient(ITranscriptStore inner) : ITranscriptStore
{
    public StoreDescriptor Describe() => inner.Describe();
    public void Open(string storeRoot, string attachmentRoot) { }
    public SessionMeta[] ListSessionsMeta() => [];
    public MessageRecord[] OpenRecent(string sessionId, int count) => throw new KeyNotFoundException(sessionId);

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count) =>
        throw new KeyNotFoundException(sessionId);

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive) =>
        throw new KeyNotFoundException(sessionId);

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars) =>
        throw new KeyNotFoundException(sessionId);

    public string FetchToolResult(string sessionId, long ordinal, int partIndex) => throw new KeyNotFoundException(sessionId);
    public long AppendMessage(string sessionId, MessageRecord message) => inner.AppendMessage(sessionId, message);
    public void StreamDraft(string sessionId, string streamId, string chunk) => inner.StreamDraft(sessionId, streamId, chunk);
    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage) => inner.FinalizeStream(sessionId, streamId, finalMessage);
    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages) => inner.ReplaceSuffix(sessionId, fromOrdinal, newMessages);
    public void CreateAssignment(AssignmentRecord assignment) => inner.CreateAssignment(assignment);
    public void SettleAssignment(SettleAssignmentRequest request) => inner.SettleAssignment(request);

    public AssignmentRecord GetAssignment(string assignmentId) => inner.GetAssignment(assignmentId);

    public void DeleteSession(string sessionId, DeletePolicy policy) { }

    public void Dispose() { }
}