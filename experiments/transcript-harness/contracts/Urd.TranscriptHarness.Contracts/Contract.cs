namespace Urd.TranscriptHarness.Contracts;

// Frozen adapter contract for the transcript-storage experiment programme.
// Adapters implement ITranscriptStore (plus IStoreStatistics optionally) and
// nothing else; the shared runner owns traces, verification and measurement.

/// <summary>What a returned operation actually promises after the call returns.</summary>
public enum AckTier
{
    /// <summary>In-memory only: acks imply nothing about survival of process death.</summary>
    MemoryOnly,
    /// <summary>Data handed to the OS on return; no fsync/journal-durability claim.</summary>
    OsFlush,
    /// <summary>Engine's strongest configured persistence (e.g. default-commit fsync).</summary>
    FsyncOrJournal,
}

/// <summary>Rules for outstanding (open) assignments when their target session is deleted.</summary>
public enum DeletePolicy
{
    /// <summary>Deletion must fail while any assignment targeting the session is open.</summary>
    RequireSettled,
    /// <summary>Deletion cancels the session's open assignments as part of the same logical operation.</summary>
    CancelOpen,
}

public sealed record StoreDescriptor(
    string StoreName,
    AckTier AckTier,
    /// <summary>Verbatim configuration affecting persistence (journal/WAL/flush flags); recorded, not set by the harness.</summary>
    string SettingsNote,
    string Notes);

/// <summary>
/// Ordinal contract (strict, verified by the oracle): ordinals are dense
/// positions 0..count-1 per session. AppendMessage assigns the next tail
/// ordinal. ReplaceSuffix(k, newMessages) keeps messages [0..k) unchanged,
/// removes everything at or after k, and renumbers newMessages at k..; a
/// replacement shorter than the removed suffix truncates the session.
/// </summary>
public interface ITranscriptStore : IDisposable
{
    StoreDescriptor Describe();

    void Open(string storeRoot, string attachmentRoot);

    SessionMeta[] ListSessionsMeta();

    /// <summary>Newest <paramref name="count"/> messages, oldest first.</summary>
    MessageRecord[] OpenRecent(string sessionId, int count);

    /// <summary>The <paramref name="count"/> messages immediately before <paramref name="beforeOrdinal"/>, oldest first.</summary>
    MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count);

    /// <summary>Messages with fromOrdinal &lt;= ordinal &lt; toOrdinalExclusive, oldest first.</summary>
    MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive);

    /// <summary>
    /// Bounded metadata/preview window for chat UI and inspector list views.
    /// Newest <paramref name="count"/> messages, oldest first, WITHOUT full
    /// payloads: per part the store reports kind, full-content character length
    /// and toolCallId if any, plus a preview string that must not exceed
    /// <paramref name="previewChars"/> characters. Returning full payload text
    /// (in particular full ToolResultPart.Content) is a contract violation.
    /// Large-result layouts must be demonstrable through this method plus
    /// FetchToolResult, never by materializing full ranges.
    /// </summary>
    WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars);

    /// <summary>Full tool-result content of one ToolResultPart (hover inspection).</summary>
    string FetchToolResult(string sessionId, long ordinal, int partIndex);

    /// <summary>Appends at the tail; returns the assigned (dense) ordinal. Acks at the descriptor's declared tier.</summary>
    long AppendMessage(string sessionId, MessageRecord message);

    /// <summary>
    /// Streams one incremental persisted batch of <paramref name="chunk"/> text
    /// for an active draft stream. Generation chunks and persistence batches are
    /// distinct: a batch may bundle several generation chunks, and every
    /// StreamDraft call IS one store write batch for the stream — stores must
    /// not silently buffer unbounded content and defer all writes to
    /// FinalizeStream (uniform write policy across candidates, so streaming
    /// write amplification is comparable). One active draft stream per session;
    /// a batch under a different streamId while one is active is an error. Each
    /// batch's text accumulates into the stream; batch behaviour across crash is
    /// the store's declared policy: nothing partial may ever be served.
    /// </summary>
    void StreamDraft(string sessionId, string streamId, string chunk);

    /// <summary>Commits the draft as an ordinary tail message; returns the assigned ordinal.
    /// The draft must exist and match <paramref name="streamId"/>.</summary>
    long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage);

    /// <summary>Linear-history edit: deletes the continuation from <paramref name="fromOrdinal"/> and
    /// renumbers newMessages in its place (see interface summary). History replacement only, never rollback.</summary>
    long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages);

    void CreateAssignment(AssignmentRecord assignment);

    void SettleAssignment(SettleAssignmentRequest request);

    AssignmentRecord GetAssignment(string assignmentId);

    /// <summary>Final-closure deletion, including large payloads, under the given assignment invariant.</summary>
    void DeleteSession(string sessionId, DeletePolicy policy);
}

/// <summary>Bounded preview window over a session's newest messages (no full payloads).</summary>
public sealed record WindowPreview(
    string SessionId,
    /// <summary>Ordinal of the oldest message in the window (dense renumbering follows edits).</summary>
    long FromOrdinal,
    IReadOnlyList<MessagePreview> Items);

public sealed record MessagePreview(long Ordinal, string Role, IReadOnlyList<PartPreview> Parts);

/// <summary>One part's metadata without full content; <see cref="Preview"/> is at most previewChars long.</summary>
public sealed record PartPreview(string Kind, long ContentChars, string? ToolCallId, string Preview);

/// <summary>Optional capability for adapters able to report their own managed allocations.</summary>
public interface IStoreStatistics
{
    StoreStatistics Sample();
}

public sealed record StoreStatistics(long ManagedAllocatedBytes);