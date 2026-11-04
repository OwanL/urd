using System.Text;
using Newtonsoft.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Plugin.TestPlugin;

/// <summary>
/// Non-native resolver-test adapter (harness-owned, not a storage candidate):
/// an in-memory store whose message state is kept as Newtonsoft.Json text
/// snapshots, so the external managed dependency is genuinely exercised on
/// every read and write. Its real purpose is to prove that the store host's
/// plugin loader resolves package dependencies (AssemblyDependencyResolver)
/// and unifies shared Contracts types with the host's own instance.
/// Semantics mirror the reference store: MemoryOnly, dense ordinals, empty
/// windows for unknown sessions, one active draft batch stream per session.
/// Never storage-evidence of any kind.
/// </summary>
public sealed class NewtonsoftInMemoryStore : ITranscriptStore
{
    private static readonly JsonSerializerSettings Settings = new() { TypeNameHandling = TypeNameHandling.Auto };

    private readonly Dictionary<string, List<string>> _sessionSnapshots = [];
    private readonly Dictionary<string, AssignmentRecord> _assignments = [];
    private readonly Dictionary<(string StreamId, string SessionId), List<string>> _draftBatches = [];
    private readonly Dictionary<string, string> _sessionDrafts = [];

    public StoreDescriptor Describe() => new(
        "TestPlugin.NewtonsoftInMemoryStore",
        AckTier.MemoryOnly,
        "n/a (in-memory resolver-test adapter; no persistence settings)",
        "In-memory store persisting each record as a Newtonsoft.Json snapshot in process memory; exists to exercise the plugin loader's dependency resolution and shared-contract type identity.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        // In-memory; nothing to open. Newtonsoft dependency is exercised below too.
        _ = JsonConvert.SerializeObject(new { opened = true }, Settings);
    }

    public SessionMeta[] ListSessionsMeta() =>
        [.. _sessionSnapshots.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SessionMeta(kv.Key, kv.Value.Count))];

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        var list = _sessionSnapshots.TryGetValue(sessionId, out var exists) ? exists : [];
        var from = Math.Max(0, list.Count - count);
        return [.. list.Skip(from).Select(Deserialize)];
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        var list = _sessionSnapshots.TryGetValue(sessionId, out var exists) ? exists : [];
        var to = Math.Clamp((int)beforeOrdinal, 0, list.Count);
        var from = Math.Max(0, to - count);
        return [.. list.GetRange(from, to - from).Select(Deserialize)];
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        if (!_sessionSnapshots.TryGetValue(sessionId, out var list))
        {
            return [];
        }
        ValidateBounds(sessionId, list, fromOrdinal, toOrdinalExclusive);
        return [.. list.GetRange((int)fromOrdinal, (int)(toOrdinalExclusive - fromOrdinal)).Select(Deserialize)];
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        var messages = OpenRecent(sessionId, count);
        var from = ListSessionsMeta().FirstOrDefault(m => m.SessionId == sessionId) is { } meta
            ? meta.MessageCount - messages.Length
            : 0;
        var items = new List<MessagePreview>();
        for (var i = 0; i < messages.Length; i++)
        {
            items.Add(PreviewOf(from + i, messages[i], previewChars));
        }
        return new WindowPreview(sessionId, from, items);
    }

    private static MessagePreview PreviewOf(long ordinal, MessageRecord message, int previewChars)
    {
        var parts = new List<PartPreview>(message.Parts.Count);
        foreach (var part in message.Parts)
        {
            var (kind, content, toolCallId) = part switch
            {
                TextPart t => ("text", t.Content, (string?)null),
                ToolCallPart c => ("toolCall", c.ArgsJson, (string?)c.ToolCallId),
                ToolResultPart r => ("toolResult", r.Content, (string?)r.ToolCallId),
                ImagePart i => ("image", i.RelativePath, (string?)null),
                _ => ("unknown", string.Empty, (string?)null),
            };
            parts.Add(new PartPreview(
                kind, content.Length, toolCallId, content[..Math.Min(content.Length, Math.Max(previewChars, 0))]));
        }
        return new MessagePreview(ordinal, message.Role, parts);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var list = Require(sessionId);
        var part = Deserialize(list[(int)ordinal]).Parts.ElementAtOrDefault(partIndex)
            ?? throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (count={list.Count})");
        return part is ToolResultPart toolResult
            ? toolResult.Content
            : throw new InvalidOperationException($"Part {partIndex} in {sessionId}@{ordinal} is {part.GetType().Name}, not a tool result");
    }

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        var list = _sessionSnapshots.TryGetValue(sessionId, out var exists) ? exists : NewSession(sessionId);
        list.Add(Serialize(message));
        return list.Count - 1;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        if (_sessionDrafts.TryGetValue(sessionId, out var active) && active != streamId)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} already has active draft {active}; only one draft per session");
        }
        if (!_draftBatches.TryGetValue((streamId, sessionId), out var batches))
        {
            batches = [];
            _draftBatches[(streamId, sessionId)] = batches;
            _sessionDrafts[sessionId] = streamId;
        }
        // Each StreamDraft call is one incremental persisted batch: the batch
        // itself goes through the Newtonsoft layer (per-batch snapshot).
        batches.Add(Serialize(new DraftBatch(streamId, chunk)));
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        // Owner check, mirroring the reference store: a stream belongs to the
        // session that opened its first batch; cross-session finalize is invalid.
        if (!_draftBatches.TryGetValue((streamId, sessionId), out var batches))
        {
            throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
        }
        _draftBatches.Remove((streamId, sessionId));
        _sessionDrafts.Remove(sessionId);
        // Batch semantics: consumed batches stay available for interpretation,
        // but the finalized message is what enters the history.
        _ = batches.Count;
        var list = _sessionSnapshots.TryGetValue(sessionId, out var exists) ? exists : NewSession(sessionId);
        list.Add(Serialize(finalMessage));
        return list.Count - 1;
    }

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        var list = Require(sessionId);
        if (fromOrdinal < 0 || fromOrdinal > list.Count)
        {
            throw new InvalidOperationException($"ReplaceSuffix from={fromOrdinal} outside count={list.Count} in {sessionId}");
        }
        list.RemoveRange((int)fromOrdinal, list.Count - (int)fromOrdinal);
        foreach (var message in newMessages) list.Add(Serialize(message));
        return list.Count;
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        if (!_assignments.TryAdd(assignment.AssignmentId, assignment))
        {
            throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
        }
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        var current = RequireAssignment(request.AssignmentId);
        _assignments[request.AssignmentId] = current with
        {
            Status = AssignmentStatus.Settled,
            ResultText = request.ResultText,
        };
    }

    public AssignmentRecord GetAssignment(string assignmentId) => RequireAssignment(assignmentId);

    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        var list = _sessionSnapshots.TryGetValue(sessionId, out var exists)
            ? exists
            : throw new InvalidOperationException($"DeleteSession of unknown session {sessionId}");
        var open = _assignments.Values
            .Where(a => a.TargetSessionId == sessionId && a.Status == AssignmentStatus.Open).ToArray();
        if (open.Length > 0)
        {
            if (policy == DeletePolicy.RequireSettled)
            {
                throw new InvalidOperationException(
                    $"DeleteSession {sessionId}: {open.Length} open assignment(s) violate the RequireSettled invariant");
            }
            foreach (var entry in open)
            {
                _assignments[entry.AssignmentId] = entry with { Status = AssignmentStatus.Cancelled };
            }
        }
        _ = list.Count;
        _sessionSnapshots.Remove(sessionId);
    }

    public void Dispose()
    {
        // In-memory; nothing to release.
    }

    // ---- helpers ----

    private List<string> NewSession(string sessionId)
    {
        var list = new List<string>();
        _sessionSnapshots[sessionId] = list;
        return list;
    }

    private List<string> Require(string sessionId) =>
        _sessionSnapshots.TryGetValue(sessionId, out var list)
            ? list
            : throw new InvalidOperationException($"Unknown session {sessionId}");

    private AssignmentRecord RequireAssignment(string assignmentId) =>
        _assignments.TryGetValue(assignmentId, out var assignment)
            ? assignment
            : throw new InvalidOperationException($"Unknown assignment {assignmentId}");

    private static void ValidateBounds(string sessionId, List<string> list, long from, long to)
    {
        if (from < 0 || to < from || to > list.Count)
        {
            throw new InvalidOperationException($"Range [{from},{to}) outside count={list.Count} in {sessionId}");
        }
    }

    private static string Serialize(object record) => JsonConvert.SerializeObject(record, Settings);

    private static MessageRecord Deserialize(string snapshot) =>
        JsonConvert.DeserializeObject<MessageRecord>(snapshot, Settings)
            ?? throw new InvalidOperationException("Empty message snapshot");

    private sealed record DraftBatch(string StreamId, string Text);
}