using System.Text;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

// The oracle: an independent expected-state model (its own in-memory replay of
// the trace) plus verification that reads the real store back through the same
// adapter contract. Verification compares full logical content, ordering and
// ordinals — never byte layout, and never an unverified digest.

public sealed class OracleModel
{
    public sealed record ExpectedMessage(string Role, IReadOnlyList<MessagePart> Parts);

    public readonly Dictionary<string, List<ExpectedMessage>> Sessions = [];
    public readonly Dictionary<string, AssignmentRecord> Assignments = [];
    public readonly HashSet<string> ActiveDrafts = [];

    public long Count(string sessionId) => Sessions.TryGetValue(sessionId, out var list) ? list.Count : 0;
}

// Expected-state transitions, returning the values a conforming store should
// report. Shared by the trace generator (consistency-by-construction) and the
// trace executor (post-hoc verification).

public static class ModelOps
{
    /// <summary>Applies an append; returns the ordinal a conforming store should report.</summary>
    public static long AppendMessage(OracleModel model, string sessionId, MessageRecord message)
    {
        // Tool-result pairing is checked oracle-side as part of the model replay.
        foreach (var part in message.Parts.OfType<ToolResultPart>())
        {
            var known = model.Sessions.TryGetValue(sessionId, out var list) &&
                        list.Any(m => m.Parts.OfType<ToolCallPart>().Any(c => c.ToolCallId == part.ToolCallId)) ||
                        message.Parts.OfType<ToolCallPart>().Any(c => c.ToolCallId == part.ToolCallId);
            if (!known)
            {
                throw new HarnessError($"Trace bug: tool result {part.ToolCallId} without a matching call");
            }
        }
        if (!model.Sessions.TryGetValue(sessionId, out var target))
        {
            target = [];
            model.Sessions[sessionId] = target;
        }
        target.Add(new OracleModel.ExpectedMessage(message.Role, message.Parts));
        return target.Count - 1;
    }

    public static long FinalizeStream(OracleModel model, string sessionId, string streamId, MessageRecord message)
    {
        if (!model.ActiveDrafts.Remove(streamId))
        {
            throw new HarnessError($"Trace bug: finalize of unknown draft {streamId}");
        }
        return AppendMessage(model, sessionId, message);
    }

    public static long ReplaceSuffix(OracleModel model, string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        var list = model.Sessions[sessionId];
        if (fromOrdinal < 0 || fromOrdinal > list.Count)
        {
            throw new HarnessError($"Trace bug: replaceSuffix from={fromOrdinal} outside count={list.Count}");
        }
        list.RemoveRange((int)fromOrdinal, list.Count - (int)fromOrdinal);
        foreach (var message in newMessages) AppendMessage(model, sessionId, message);
        return list.Count;
    }

    /// <summary>Expected logical window [from, toExclusive).</summary>
    public static MessageRecord[] WindowOf(OracleModel model, string sessionId, long from, long? toExclusive)
    {
        var list = model.Sessions.TryGetValue(sessionId, out var value)
            ? value
            : throw new HarnessVerificationError($"Unknown expected session {sessionId}");
        var end = Math.Min(toExclusive ?? list.Count, list.Count);
        if (from < 0 || from > end)
        {
            throw new HarnessVerificationError($"Expected window [{from}, {end}) invalid for {sessionId} (count {list.Count})");
        }
        return [.. list.GetRange((int)from, (int)(end - from)).Select(m => new MessageRecord(m.Role, m.Parts))];
    }

    public static void CreateAssignment(OracleModel model, AssignmentRecord assignment)
    {
        if (!model.Assignments.TryAdd(assignment.AssignmentId, assignment))
        {
            throw new HarnessError($"Duplicate assignment id {assignment.AssignmentId}");
        }
        if (!model.Sessions.ContainsKey(assignment.TargetSessionId))
        {
            throw new HarnessVerificationError(
                $"Assignment {assignment.AssignmentId} targets absent session {assignment.TargetSessionId}");
        }
    }

    public static void SettleAssignment(OracleModel model, SettleAssignmentRequest request)
    {
        model.Assignments[request.AssignmentId] = RequireAssignment(model, request.AssignmentId) with
        {
            Status = AssignmentStatus.Settled,
            ResultText = request.ResultText,
        };
    }

    public static void DeleteSession(OracleModel model, string sessionId, DeletePolicy policy)
    {
        if (!model.Sessions.ContainsKey(sessionId))
        {
            throw new HarnessError($"DeleteSession of unknown session {sessionId}");
        }
        var open = model.Assignments.Values
            .Where(a => a.TargetSessionId == sessionId && a.Status == AssignmentStatus.Open).ToArray();
        if (open.Length > 0)
        {
            if (policy == DeletePolicy.RequireSettled)
            {
                throw new HarnessVerificationError(
                    $"DeleteSession {sessionId} with {open.Length} open assignment(s) under RequireSettled: " +
                    "assignment/deletion invariant would be violated (dangling open references are failures, not tolerated state)");
            }
            foreach (var assignment in open)
            {
                model.Assignments[assignment.AssignmentId] = assignment with { Status = AssignmentStatus.Cancelled };
            }
        }
        model.Sessions.Remove(sessionId);
    }

    private static AssignmentRecord RequireAssignment(OracleModel model, string assignmentId) =>
        model.Assignments.TryGetValue(assignmentId, out var value)
            ? value
            : throw new HarnessError($"Unknown assignment {assignmentId}");
}

public static class Oracle
{
    /// <summary>
    /// Full logical verification of the real store against the expected model.
    /// Content is streamed back in bounded windows through the adapter's read
    /// surface; the oracle never assumes a digest is correct, and never holds
    /// its expectations inside the store process.
    /// </summary>
    public static void VerifyAll(ITranscriptStore client, OracleModel model, int windowSize = 400)
    {
        VerifySessionIndex(client.ListSessionsMeta(), model);

        foreach (var meta in client.ListSessionsMeta())
        {
            if (!model.Sessions.TryGetValue(meta.SessionId, out var expected))
            {
                throw new HarnessVerificationError($"Store unexpectedly retains deleted session {meta.SessionId}");
            }
            if (meta.MessageCount != expected.Count)
            {
                throw new HarnessVerificationError(
                    $"Session {meta.SessionId} count {meta.MessageCount} != expected {expected.Count}");
            }
            var from = 0L;
            while (from < meta.MessageCount)
            {
                var to = Math.Min(from + windowSize, meta.MessageCount);
                var actual = client.ReadRange(meta.SessionId, from, to);
                if (actual.Length != to - from)
                {
                    throw new HarnessVerificationError(
                        $"Session {meta.SessionId} window [{from},{to}): store returned {actual.Length} records");
                }
                for (var i = 0; i < actual.Length; i++)
                {
                    CompareMessage(actual[i], expected[(int)(from + i)], meta.SessionId, from + i);
                }
                from = to;
            }

            // Verify every tool result by full-content fetch (hover path).
            for (var ordinal = 0L; ordinal < meta.MessageCount; ordinal++)
            {
                var expectedMessage = expected[(int)ordinal];
                for (var partIndex = 0; partIndex < expectedMessage.Parts.Count; partIndex++)
                {
                    if (expectedMessage.Parts[partIndex] is not ToolResultPart toolResult) continue;
                    var content = client.FetchToolResult(meta.SessionId, ordinal, partIndex);
                    if (!string.Equals(content, toolResult.Content, StringComparison.Ordinal))
                    {
                        throw new HarnessVerificationError(
                            $"Tool result {toolResult.ToolCallId} in {meta.SessionId}@{ordinal}[{partIndex}] content mismatch " +
                            $"(len {content.Length} vs expected {toolResult.Content.Length})");
                    }
                }
            }

            // Bounded metadata/preview read (chat-list path): verified against the
            // model without materializing full payloads.
            var previewCount = (int)Math.Min(20, meta.MessageCount);
            if (previewCount > 0)
            {
                var previewFrom = meta.MessageCount - previewCount;
                ComparePreviewWindow(
                    client.OpenRecentPreview(meta.SessionId, previewCount, 24),
                    expected, previewFrom, 24, meta.SessionId);
            }
        }

        foreach (var assignmentId in model.Assignments.Keys)
        {
            var expectedAssignment = model.Assignments[assignmentId];
            if (!model.Sessions.ContainsKey(expectedAssignment.TargetSessionId) &&
                expectedAssignment.Status == AssignmentStatus.Open)
            {
                throw new HarnessVerificationError(
                    $"Dangling open assignment {assignmentId} targets deleted session {expectedAssignment.TargetSessionId}");
            }
            var actualAssignment = client.GetAssignment(assignmentId);
            if (actualAssignment.Status != expectedAssignment.Status ||
                !string.Equals(actualAssignment.ResultText, expectedAssignment.ResultText, StringComparison.Ordinal))
            {
                throw new HarnessVerificationError(
                    $"Assignment {assignmentId} state {actualAssignment.Status}/'{actualAssignment.ResultText}' " +
                    $"!= expected {expectedAssignment.Status}/'{expectedAssignment.ResultText}'");
            }
        }
    }

    /// <summary>Compares one read result against the expected window in full.</summary>
    public static void CompareWindow(MessageRecord[] actual, MessageRecord[] expected, string label)
    {
        if (actual.Length != expected.Length)
        {
            throw new HarnessVerificationError($"{label}: {actual.Length} records != expected {expected.Length}");
        }
        for (var i = 0; i < actual.Length; i++)
        {
            var expectedMessage = new OracleModel.ExpectedMessage(expected[i].Role, expected[i].Parts);
            CompareMessage(actual[i], expectedMessage, label, i);
        }
    }

    /// <summary>
    /// Compares a bounded preview window against the model: ordinals, roles,
    /// per-part kind/length/toolCallId and length-capped preview text. The
    /// preview must never carry full payload text (this check enforces the cap).
    /// </summary>
    public static void ComparePreviewWindow(
        WindowPreview actual, List<OracleModel.ExpectedMessage> expected, long expectedFrom, int previewChars, string sessionId)
    {
        if (actual.SessionId != sessionId)
            throw new HarnessVerificationError($"Preview window session {actual.SessionId} != {sessionId}");
        if (actual.FromOrdinal != expectedFrom)
            throw new HarnessVerificationError($"Preview window from {actual.FromOrdinal} != {expectedFrom} in {sessionId}");
        if (actual.Items.Count != expected.Count - expectedFrom)
        {
            throw new HarnessVerificationError($"Preview window items {actual.Items.Count} != expected {expected.Count - expectedFrom} in {sessionId}");
        }
        for (var i = 0; i < actual.Items.Count; i++)
        {
            var item = actual.Items[i];
            var expectedMessage = expected[(int)(expectedFrom + i)];
            if (item.Ordinal != expectedFrom + i) throw new HarnessVerificationError($"Preview ordinal {item.Ordinal} != {expectedFrom + i} in {sessionId}");
            if (item.Role != expectedMessage.Role) throw new HarnessVerificationError($"Preview role {item.Role} != {expectedMessage.Role} in {sessionId}@{item.Ordinal}");
            if (item.Parts.Count != expectedMessage.Parts.Count)
            {
                throw new HarnessVerificationError($"Preview parts {item.Parts.Count} != {expectedMessage.Parts.Count} in {sessionId}@{item.Ordinal}");
            }
            for (var j = 0; j < item.Parts.Count; j++)
            {
                var actualPart = item.Parts[j];
                var (kind, content, toolCallId) = expectedMessage.Parts[j] switch
                {
                    TextPart t => ("text", t.Content, (string?)null),
                    ToolCallPart c => ("toolCall", c.ArgsJson, (string?)c.ToolCallId),
                    ToolResultPart r => ("toolResult", r.Content, (string?)r.ToolCallId),
                    ImagePart im => ("image", im.RelativePath, (string?)null),
                    _ => throw new HarnessVerificationError($"Preview of unexpected part type in {sessionId}@{item.Ordinal}[{j}]"),
                };
                if (actualPart.Kind != kind || actualPart.ContentChars != content.Length || actualPart.ToolCallId != toolCallId)
                {
                    throw new HarnessVerificationError(
                        $"Preview part metadata mismatch in {sessionId}@{item.Ordinal}[{j}]: " +
                        $"{actualPart.Kind}/{actualPart.ContentChars}/{actualPart.ToolCallId ?? "-"} != {kind}/{content.Length}/{toolCallId ?? "-"}");
                }
                var cappedPreview = content[..Math.Min(content.Length, previewChars)];
                if (actualPart.Preview.Length > previewChars || !string.Equals(actualPart.Preview, cappedPreview, StringComparison.Ordinal))
                {
                    throw new HarnessVerificationError(
                        $"Preview text mismatch in {sessionId}@{item.Ordinal}[{j}] (len {actualPart.Preview.Length}, must not exceed {previewChars})");
                }
            }
        }
    }

    public static void VerifySessionIndex(SessionMeta[] metas, OracleModel model)
    {
        var actualIndex = metas.OrderBy(m => m.SessionId, StringComparer.Ordinal).ToArray();
        var expectedIndex = model.Sessions
            .Select(kv => new SessionMeta(kv.Key, kv.Value.Count))
            .OrderBy(m => m.SessionId, StringComparer.Ordinal).ToArray();
        if (actualIndex.Length != expectedIndex.Length ||
            actualIndex.Zip(expectedIndex)
                .Any(p => p.First.SessionId != p.Second.SessionId || p.First.MessageCount != p.Second.MessageCount))
        {
            throw new HarnessVerificationError(
                $"Session index mismatch: store [{string.Join(", ", actualIndex.Select(m => $"{m.SessionId}:{m.MessageCount}"))}] " +
                $"expected [{string.Join(", ", expectedIndex.Select(m => $"{m.SessionId}:{m.MessageCount}"))}]");
        }
    }

    /// <summary>
    /// Post-kill acceptance: a commit whose acknowledgement was lost may
    /// legitimately have persisted, so an unacknowledged atomic mutation
    /// resolves to the before-state or the fully committed after-state — never
    /// partially applied state. Stores declaring MemoryOnly acks cannot be
    /// judged by either model; only structural coherence is accepted, and no
    /// durability claim may be reported from them.
    /// Returns true when the before/after state was accepted by content
    /// comparison (persistent tiers only).
    /// </summary>
    public static bool VerifyAfterCrash(
        ITranscriptStore client, OracleModel baseline, AckTier tier, OracleModel? withInflight, int windowSize = 400)
    {
        if (tier == AckTier.MemoryOnly)
        {
            VerifyStructuralCoherence(client, windowSize);
            return false; // no content verdict is possible for a memory-only tier
        }
        try
        {
            VerifyAll(client, baseline, windowSize);
            return true;
        }
        catch (HarnessVerificationError)
        {
            if (withInflight == null) throw;
        }
        VerifyAll(client, withInflight, windowSize);
        return true;
    }

    private static void VerifyStructuralCoherence(ITranscriptStore client, int windowSize)
    {
        foreach (var meta in client.ListSessionsMeta())
        {
            if (meta.MessageCount < 0)
            {
                throw new HarnessVerificationError($"Session {meta.SessionId} reports negative count {meta.MessageCount}");
            }
            var from = 0L;
            while (from < meta.MessageCount)
            {
                var to = Math.Min(from + windowSize, meta.MessageCount);
                var window = client.ReadRange(meta.SessionId, from, to);
                for (var i = 0; i < window.Length; i++)
                {
                    if (window[i].Parts.Count == 0)
                    {
                        throw new HarnessVerificationError(
                            $"Session {meta.SessionId}@{from + i} has no parts (partially applied record)");
                    }
                }
                from = to;
            }
        }
    }

    private static void CompareMessage(MessageRecord actual, OracleModel.ExpectedMessage expected, string label, long ordinal)
    {
        if (actual.Role != expected.Role)
        {
            throw new HarnessVerificationError($"{label}@{ordinal} role '{actual.Role}' != expected '{expected.Role}'");
        }
        if (actual.Parts.Count != expected.Parts.Count)
        {
            throw new HarnessVerificationError($"{label}@{ordinal} part count {actual.Parts.Count} != expected {expected.Parts.Count}");
        }
        for (var i = 0; i < actual.Parts.Count; i++)
        {
            if (!PartEquals(actual.Parts[i], expected.Parts[i]))
            {
                throw new HarnessVerificationError(
                    $"{label}@{ordinal}[{i}] content mismatch: '{Describe(actual.Parts[i])}' != expected '{Describe(expected.Parts[i])}'");
            }
        }
    }

    private static string Describe(MessagePart part) => part switch
    {
        TextPart t => $"text[{Truncate(t.Content)}]",
        ToolCallPart c => $"call[{c.ToolCallId},{c.Name}]",
        ToolResultPart r => $"result[{r.ToolCallId},{Truncate(r.Content)}]",
        ImagePart i => $"image[{i.ImageId},{i.RelativePath}]",
        _ => part.GetType().Name,
    };

    private static string Truncate(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Take(60)) sb.Append(char.IsControl(ch) ? '?' : ch);
        return sb.Length < text.Length ? sb.ToString() + "\u2026" : sb.ToString();
    }

    private static bool PartEquals(MessagePart a, MessagePart b) => (a, b) switch
    {
        (TextPart x, TextPart y) => string.Equals(x.Content, y.Content, StringComparison.Ordinal),
        (ToolCallPart x, ToolCallPart y) => x.ToolCallId == y.ToolCallId && x.Name == y.Name && x.ArgsJson == y.ArgsJson,
        (ToolResultPart x, ToolResultPart y) => x.ToolCallId == y.ToolCallId && string.Equals(x.Content, y.Content, StringComparison.Ordinal),
        (ImagePart x, ImagePart y) => x.ImageId == y.ImageId && x.RelativePath == y.RelativePath,
        (null, null) => true,
        _ => false,
    };
}