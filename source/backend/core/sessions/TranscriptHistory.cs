using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Stable entry identity; text entries retain their MessageRef identity for content reads.
public sealed record TranscriptEntryRef(
    Guid ExecutionId, Guid EntryId, TranscriptEntryKind Kind, long HistoryRevision);

public enum TranscriptEntryKind { User, Text, Tool }

public enum TranscriptHistoryMode { Latest, Before, After, Around }

public sealed record TranscriptHistoryQuery(
    TranscriptHistoryMode Mode, long ExpectedHistoryRevision, TranscriptEntryRef? Anchor = null);

// Metadata only. Neither message text nor tool arguments/results belong in a history page.
public sealed record TranscriptEntryDescriptor(
    TranscriptEntryRef Entry, DateTimeOffset? CreatedAt, TranscriptTextFacts? Text, TranscriptToolFacts? Tool);

public sealed record TranscriptTextFacts(
    MessageContentCursor From, MessageContentCursor AvailableThrough, bool IsClosed, bool FinalOutputCommitted);

// A closed, never-started reservation was not executed. Missing timing/outcome facts stay unknown.
public sealed record TranscriptToolFacts(
    Guid RequestId, string CallId, string? Name, bool Started, ExecutionOutcome? Outcome,
    bool IsClosed, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

public sealed record TranscriptHistoryPage(
    Guid SessionId, long HistoryRevision, IReadOnlyList<TranscriptEntryDescriptor> Entries,
    bool HasBefore, bool HasAfter, IReadOnlyList<SessionExecutionResult> Settlements);
