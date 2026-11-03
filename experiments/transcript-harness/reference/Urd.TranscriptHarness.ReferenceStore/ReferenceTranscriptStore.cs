using System.Text;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.ReferenceStore;

// Fake in-memory reference adapter for runner self-tests: implements the frozen
// contract exactly at the logical level and promises nothing about
// process-death survival (MemoryOnly). It must never be the basis of any
// durability claim, and kill tests against it validate harness mechanics only.
// Read semantics: reads on unknown or empty sessions return an empty window
// (absence is not an error); mutations on unknown sessions create them,
// except deletes, which must name a real session.
// Preview semantics: OpenRecentPreview returns metadata/preview only; full
// payload text is fetched separately via FetchToolResult. StreamDraft batches:
// every call accumulates one incremental persisted batch per stream (reference
// store accumulates in memory only); conflicting stream ids are an error.

public sealed class ReferenceTranscriptStore : ITranscriptStore, IStoreStatistics
{
    private readonly Dictionary<string, List<MessageRecord>> _sessions = [];
    private readonly Dictionary<string, AssignmentRecord> _assignments = [];

    // streamId -> (owning session, accumulated draft text)
    private readonly Dictionary<string, (string SessionId, StringBuilder Text)> _drafts = [];
    // session -> streamId, to enforce one active draft per session
    private readonly Dictionary<string, string> _sessionDrafts = [];

    public StoreDescriptor Describe() => new(
        "ReferenceStore",
        AckTier.MemoryOnly,
        "n/a (in-memory reference; no persistence settings)",
        "Fake in-memory store for runner self-tests; large results stay inline; drafts vanish on process death.");

    public void Open(string storeRoot, string attachmentRoot) { }

    public SessionMeta[] ListSessionsMeta() =>
        [.. _sessions.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new SessionMeta(kv.Key, kv.Value.Count))];

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        var list = _sessions.TryGetValue(sessionId, out var exists) ? exists : [];
        var from = Math.Max(0, list.Count - count);
        return [.. list.Skip((int)from)];
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        var list = _sessions.TryGetValue(sessionId, out var exists) ? exists : [];
        var to = Math.Clamp((int)beforeOrdinal, 0, list.Count);
        var from = Math.Max(0, to - count);
        return [.. list.GetRange(from, to - from)];
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        if (!_sessions.TryGetValue(sessionId, out var list))
        {
            return [];
        }
        ValidateBounds(sessionId, list, fromOrdinal, toOrdinalExclusive);
        return [.. list.GetRange((int)fromOrdinal, (int)(toOrdinalExclusive - fromOrdinal))];
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        var list = _sessions.TryGetValue(sessionId, out var exists) ? exists : [];
        var from = Math.Max(0, list.Count - count);
        var items = new List<MessagePreview>(list.Count - from);
        for (var i = from; i < list.Count; i++) items.Add(PreviewOf(i, list[i], previewChars));
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
        var part = list[(int)ordinal].Parts.ElementAtOrDefault(partIndex)
            ?? throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (count={list.Count}, parts={list[(int)ordinal].Parts.Count})");
        return part is ToolResultPart toolResult
            ? toolResult.Content
            : throw new InvalidOperationException($"Part {partIndex} in {sessionId}@{ordinal} is {part.GetType().Name}, not a tool result");
    }

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        var list = ListFor(sessionId);
        list.Add(message);
        return list.Count - 1;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        if (_sessionDrafts.TryGetValue(sessionId, out var active) && active != streamId)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} already has active draft {active}; only one draft per session");
        }
        if (!string.IsNullOrEmpty(chunk))
        {
            if (!_drafts.TryGetValue(streamId, out var draft))
            {
                draft = (sessionId, new StringBuilder());
                _drafts[streamId] = draft;
                _sessionDrafts[sessionId] = streamId;
            }
            draft.Text.Append(chunk);
        }
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        if (!_drafts.TryGetValue(streamId, out var draft) || draft.SessionId != sessionId)
        {
            throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
        }
        _drafts.Remove(streamId);
        _sessionDrafts.Remove(sessionId);
        var list = ListFor(sessionId);
        list.Add(finalMessage);
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
        foreach (var message in newMessages) list.Add(message);
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
        var list = _sessions.TryGetValue(sessionId, out var exists)
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
            foreach (var assignment in open)
            {
                _assignments[assignment.AssignmentId] = assignment with { Status = AssignmentStatus.Cancelled };
            }
        }
        list.Clear(); // reference store: memory only, no cleanup footprint
        _sessions.Remove(sessionId);
    }

    public StoreStatistics Sample() => new(GC.GetTotalAllocatedBytes(precise: false));

    public void Dispose()
    {
        // In-memory reference store; nothing to release beyond the dictionaries.
    }

    private List<MessageRecord> ListFor(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var list))
        {
            list = [];
            _sessions[sessionId] = list;
        }
        return list;
    }

    private List<MessageRecord> Require(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var list)
            ? list
            : throw new InvalidOperationException($"Unknown session {sessionId}");

    private static void ValidateBounds(string sessionId, List<MessageRecord> list, long from, long to)
    {
        if (from < 0 || to < from || to > list.Count)
        {
            throw new InvalidOperationException($"Range [{from},{to}) outside count={list.Count} in {sessionId}");
        }
    }

    private AssignmentRecord RequireAssignment(string assignmentId) =>
        _assignments.TryGetValue(assignmentId, out var assignment)
            ? assignment
            : throw new InvalidOperationException($"Unknown assignment {assignmentId}");
}