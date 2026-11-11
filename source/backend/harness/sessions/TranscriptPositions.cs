using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Abstract source-size units, not pixels. Geometry belongs to the browser; measurements
// must never be written back as weights. Rank is dense and zero-based within a revision.
public sealed record TranscriptPosition(
    TranscriptEntryRef Entry, long Rank, long Weight, long PrefixWeight);

public sealed record TranscriptPositionState(
    Guid SessionId, long HistoryRevision, long PositionVersion, long EntryCount, long TotalWeight);

public sealed record TranscriptPositionedEntry(TranscriptEntryDescriptor Descriptor, TranscriptPosition Position);

// Target is null only for an empty history. The window is oldest first, at most 60 entries.
public sealed record TranscriptPositionWindow(
    TranscriptPositionState State, TranscriptPosition? Target,
    IReadOnlyList<TranscriptPositionedEntry> Entries, bool HasBefore, bool HasAfter,
    IReadOnlyList<SessionExecutionResult> Settlements);

// NextAfterRank is an exclusive keyset cursor in this history revision, not an ordinal.
public sealed record TranscriptPromptPage(
    TranscriptPositionState State, IReadOnlyList<TranscriptPosition> Prompts, long? NextAfterRank);
