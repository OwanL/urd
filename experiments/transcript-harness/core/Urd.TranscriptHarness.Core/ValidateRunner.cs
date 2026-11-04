using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

/// <summary>
/// Persistence validation over the frozen contract (no contract changes and no
/// extra store API): a small deterministic acked-prefix workload is written
/// (appends plus one streamed batch), then two independent checks run against
/// real reopen behaviour.
/// 1. clean-reopen: graceful close, fresh host process over the same store
///    root, then FULL content verification against the baseline model.
/// 2. kill-reopen: process kill with one unacknowledged mutation in flight;
///    after reopen the session must equal the baseline or the
///    baseline-plus-inflight end state — never partial content. The verdict is
///    by content comparison, never by the child merely exiting.
/// </summary>
/// <remarks>
/// Adapters declaring <see cref="AckTier.MemoryOnly"/> cannot be judged for
/// persistence: their runs are informational only — verification falls back to
/// structural coherence and results must never be reported as durable evidence.
/// </remarks>
public static class ValidateRunner
{
    public sealed record Outcome(
        AckTier Tier,
        bool DurableJudgementPossible,
        bool CleanReopenVerified,
        bool KillReopenVerified,
        string? KillResolution,
        IReadOnlyList<string> Notes);

    /// <summary>Runs both validation checks; exit codes are the caller's concern.</summary>
    public static Outcome Run(IpcStoreAdapter.StoreHostLaunch launch, int messageCount)
    {
        var notes = new List<string>();
        using var adapter = IpcStoreAdapter.Spawn(launch);
        var descriptor = adapter.Describe();

        var baseline = new OracleModel();
        const string sessionId = "validate";

        for (var i = 0; i < messageCount; i++)
        {
            var message = Message(i);
            if (i % 5 == 0)
            {
                // Exercise the streaming batch path within the acked prefix.
                var streamId = $"stream-{i}";
                foreach (var chunk in new[] { "alpha ", "beta " })
                {
                    adapter.StreamDraft(sessionId, streamId, chunk);
                    baseline.ActiveDrafts.Add(streamId);
                }
                ModelOps.FinalizeStream(baseline, sessionId, streamId, message);
                var streamedOrdinal = adapter.FinalizeStream(sessionId, streamId, message);
                Require(streamedOrdinal == baseline.Count(sessionId) - 1,
                    $"finalize ordinal {streamedOrdinal} != {baseline.Count(sessionId) - 1}");
                continue;
            }
            var expected = ModelOps.AppendMessage(baseline, sessionId, message);
            var appended = adapter.AppendMessage(sessionId, message);
            Require(appended == expected, $"append ordinal {appended} != {expected}");
        }

        // Assignment coverage so settled invariant state is part of the checked content.
        var assignment = new AssignmentRecord("validate-asg", sessionId, "validation", AssignmentStatus.Settled, "done");
        adapter.CreateAssignment(assignment);
        ModelOps.CreateAssignment(baseline, assignment);

        Oracle.VerifyAll(adapter, baseline);

        // ---- Check 1: graceful close, fresh host process, full content. ----
        adapter.Close();
        adapter.Dispose();
        var reopen = IpcStoreAdapter.Spawn(launch);
        bool cleanVerified;
        try
        {
            if (descriptor.AckTier == AckTier.MemoryOnly)
            {
                VerifyStructural(reopen);
                cleanVerified = false;
                notes.Add("MemoryOnly: reopened-empty content is expected; not persistence evidence");
            }
            else
            {
                Oracle.VerifyAll(reopen, baseline);
                cleanVerified = true;
            }
        }
        finally
        {
            reopen.Close();
            reopen.Dispose();
        }

        // ---- Check 2: kill with one mutation in flight, accept before/after. ----
        var killer = IpcStoreAdapter.Spawn(launch);
        string? resolution = null;
        var killVerified = false;
        try
        {
            killer.SendRaw(RawAppend(sessionId, "inflight-marker-<kill>"));
            Thread.Sleep(120); // let the unacknowledged op land (or not) nondeterministically
            killer.KillNow();
            killer.Process.WaitForExit();
            killer.Dispose();

            // Candidate end state: baseline plus the in-flight append.
            var withInflight = new OracleModel();
            foreach (var kv in baseline.Sessions)
            {
                foreach (var expectedMessage in kv.Value)
                {
                    ModelOps.AppendMessage(withInflight, kv.Key, new MessageRecord(expectedMessage.Role, expectedMessage.Parts));
                }
            }
            ModelOps.AppendMessage(withInflight, sessionId, Message(20000, "inflight-marker-<kill>"));

            var reopenAfterKill = IpcStoreAdapter.Spawn(launch);
            try
            {
                if (descriptor.AckTier == AckTier.MemoryOnly)
                {
                    VerifyStructural(reopenAfterKill);
                    resolution = "structural-only";
                    killVerified = false;
                    notes.Add("MemoryOnly: kill check is mechanics only; no content verdict possible");
                }
                else
                {
                    var beforeError = TryVerifyAll(reopenAfterKill, baseline);
                    var afterError = beforeError is null ? null : TryVerifyAll(reopenAfterKill, withInflight);
                    if (beforeError is null)
                    {
                        resolution = "before-state accepted";
                        killVerified = true;
                    }
                    else if (afterError is null)
                    {
                        resolution = "after-state accepted (unacked commit present)";
                        killVerified = true;
                    }
                    else
                    {
                        throw new HarnessVerificationError(
                            $"kill reopen resolved to neither before- nor after-state; baseline mismatch: {beforeError.Message}");
                    }
                }
            }
            finally
            {
                reopenAfterKill.Close();
                reopenAfterKill.Dispose();
            }
        }
        finally
        {
            killer.Dispose();
        }

        return new Outcome(
            descriptor.AckTier,
            descriptor.AckTier != AckTier.MemoryOnly,
            cleanVerified,
            killVerified,
            resolution,
            notes);
    }

    private static Exception? TryVerifyAll(IpcStoreAdapter client, OracleModel model)
    {
        try
        {
            Oracle.VerifyAll(client, model);
            return null;
        }
        catch (Exception ex) when (ex is HarnessVerificationError or HarnessError)
        {
            return ex;
        }
    }

    /// <summary>Structural-coherence-only check (Oracle.VerifyAfterCrash's MemoryOnly path).</summary>
    private static void VerifyStructural(IpcStoreAdapter client)
    {
        Oracle.VerifyAfterCrash(client, new OracleModel(), AckTier.MemoryOnly, null);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new HarnessVerificationError(message);
    }

    private static MessageRecord Message(int seed, string? forcedContent = null)
    {
        var content = forcedContent ?? $"validate-content-{seed:D4}-" + new string('t', 160);
        var parts = new List<MessagePart> { new TextPart(content) };
        if (seed % 3 == 1)
        {
            parts.Add(new ToolCallPart($"call-{seed}", "probe", "{}"));
            parts.Add(new ToolResultPart($"call-{seed}", $"validate-tool-{seed:D4}-" + new string('r', 300)));
        }
        return new MessageRecord(seed % 2 == 0 ? "user" : "assistant", parts);
    }

    private static string RawAppend(string sessionId, string content) =>
        $"{{\"i\":910001,\"op\":\"append\",\"p\":{{\"sessionId\":\"{sessionId}\",\"message\":{{\"role\":\"user\",\"parts\":[{{\"kind\":\"text\",\"content\":{System.Text.Json.JsonSerializer.Serialize(content)}}}]}}}}}}";
}