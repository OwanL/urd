using System.Text.Json.Serialization;
using Urd.Core.Execution;
using Urd.Core.Compaction;

namespace Urd.Core.Sessions;

// Stable entry identity; text entries retain their MessageRef identity for content reads.
public sealed record TranscriptEntryRef(
    Guid ExecutionId, Guid EntryId, TranscriptEntryKind Kind, long HistoryRevision);

public enum TranscriptEntryKind { User = 0, Text = 1, Tool = 2, ToolCompletion = 3, Compaction = 4 }

public enum TranscriptHistoryMode { Latest, Before, After, Around }

public sealed record TranscriptHistoryQuery(
    TranscriptHistoryMode Mode, long ExpectedHistoryRevision, TranscriptEntryRef? Anchor = null);

// Metadata only. Neither message text nor tool arguments/results belong in a history page.
// Response headers appear once per execution; preparation summaries are anchored to their boundary.
public sealed record TranscriptEntryDescriptor(
    TranscriptEntryRef Entry, DateTimeOffset? CreatedAt, TranscriptTextFacts? Text, TranscriptToolFacts? Tool,
    TranscriptCompactionFacts? Compaction = null, ResponseHeaderInfo? Response = null,
    IReadOnlyList<PrepassSummary>? Preparation = null, TranscriptAgentQuestionFacts? AgentQuestion = null);

public sealed record TranscriptAgentQuestionFacts(string? SenderTitle);

public sealed record TranscriptCompactionFacts(CompactionReason Reason, long EstimatedContextTokensBefore, long EstimatedContextTokensAfter);

public sealed record TranscriptTextFacts(
    MessageContentCursor From, MessageContentCursor AvailableThrough, bool IsClosed, bool FinalOutputCommitted);

// A closed, never-started reservation was not executed. Missing timing/outcome facts stay unknown.
public sealed record TranscriptToolFacts(
    Guid RequestId, string CallId, string? Name, bool Started, bool Interrupted, ExecutionOutcome? Outcome,
    bool IsClosed, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    bool Background, bool BatchedWithPrevious, bool BatchedWithNext,
    [property: JsonIgnore] string? ArgumentsExcerpt);

public sealed record TranscriptHistoryPage(
    Guid SessionId, long HistoryRevision, IReadOnlyList<TranscriptEntryDescriptor> Entries,
    bool HasBefore, bool HasAfter, IReadOnlyList<SessionExecutionResult> Settlements);
