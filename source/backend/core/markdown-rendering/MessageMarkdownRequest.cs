using System.Text;
using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

// A lightweight immutable snapshot, never an assembled source. Captures use
// committed segment boundaries, including while the message is still open.
internal sealed record MessageMarkdownKey(
    Guid SessionId, MessageRef Message, long FromSegment, long ThroughSegment,
    bool IsClosed, bool FinalOutputCommitted, int SourceUtf8Bytes,
    string Pipeline = MarkdownRenderIndex.PipelineVersion) {
  internal TranscriptTextFacts TextFacts => new(
      new(Message, FromSegment, 0), new(Message, ThroughSegment, 0), IsClosed, FinalOutputCommitted);

  // Only server-produced positioned entries carry authoritative source-byte weights.
  internal static MessageMarkdownKey FromPositionedEntry(Guid sessionId, TranscriptPositionedEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (sessionId == Guid.Empty)
      throw InvalidEntry(nameof(entry), "A session identity is required.");

    TranscriptEntryDescriptor descriptor = entry.Descriptor
        ?? throw InvalidEntry(nameof(entry), "A positioned entry requires a descriptor.");
    TranscriptPosition position = entry.Position
        ?? throw InvalidEntry(nameof(entry), "A positioned entry requires a position.");
    TranscriptEntryRef descriptorEntry = descriptor.Entry
        ?? throw InvalidEntry(nameof(entry), "A descriptor requires an entry identity.");
    TranscriptEntryRef positionEntry = position.Entry
        ?? throw InvalidEntry(nameof(entry), "A position requires an entry identity.");
    if (descriptorEntry != positionEntry)
      throw InvalidEntry(nameof(entry), "The descriptor and position identities do not match.");
    if (descriptorEntry.ExecutionId == Guid.Empty || descriptorEntry.EntryId == Guid.Empty
        || descriptorEntry.HistoryRevision < 0 || !Enum.IsDefined(descriptorEntry.Kind)
        || descriptorEntry.Kind is not (TranscriptEntryKind.User or TranscriptEntryKind.Text))
      throw InvalidEntry(nameof(entry), "The positioned entry is not a supported text entry.");
    if (descriptor.Tool is not null)
      throw InvalidEntry(nameof(entry), "A text entry cannot contain tool facts.");

    TranscriptTextFacts facts = descriptor.Text
        ?? throw InvalidEntry(nameof(entry), "A text entry requires text facts.");
    MessageContentCursor from = facts.From
        ?? throw InvalidEntry(nameof(entry), "Text facts require a source start.");
    MessageContentCursor through = facts.AvailableThrough
        ?? throw InvalidEntry(nameof(entry), "Text facts require a source end.");
    MessageRef message = from.Message
        ?? throw InvalidEntry(nameof(entry), "The source start requires a message identity.");
    if (through.Message is null || through.Message != message || message.ExecutionId != descriptorEntry.ExecutionId
        || message.MessageId != descriptorEntry.EntryId || message.HistoryRevision != descriptorEntry.HistoryRevision
        || message.Role != (descriptorEntry.Kind == TranscriptEntryKind.User ? MessageRole.User : MessageRole.Assistant)
        || !Enum.IsDefined(message.Role))
      throw InvalidEntry(nameof(entry), "The text source identity does not match its transcript entry.");
    if (facts.FinalOutputCommitted && !facts.IsClosed)
      throw InvalidEntry(nameof(entry), "Committed final output must be closed.");
    if (descriptorEntry.Kind == TranscriptEntryKind.User
        && (!facts.IsClosed || facts.FinalOutputCommitted || from.SegmentIndex != 0 || through.SegmentIndex != 1))
      throw InvalidEntry(nameof(entry), "User text must describe its complete immutable source extent.");
    long sourceSegmentCount = CheckedSubtract(through.SegmentIndex, from.SegmentIndex, nameof(entry));
    if (from.SegmentIndex < 0 || sourceSegmentCount <= 0 || from.Utf8Offset != 0 || through.Utf8Offset != 0)
      throw InvalidEntry(nameof(entry), "Text facts must describe an ordered range of complete segments.");

    if (position.Rank < 0 || position.PrefixWeight < 0 || position.Weight < 256)
      throw InvalidEntry(nameof(entry), "The transcript position has invalid rank or weight values.");
    _ = CheckedAdd(position.Rank, 1, nameof(entry));
    _ = CheckedAdd(position.PrefixWeight, position.Weight, nameof(entry));
    long sourceUtf8Bytes = position.Weight - 256;
    if (sourceUtf8Bytes > int.MaxValue)
      throw InvalidEntry(nameof(entry), "The source exceeds the supported UTF-8 byte count.");

    var key = new MessageMarkdownKey(sessionId, message, from.SegmentIndex, through.SegmentIndex,
        facts.IsClosed, facts.FinalOutputCommitted, (int)sourceUtf8Bytes);
    key.Validate();
    return key;
  }

  internal void Validate() {
    if (SessionId == Guid.Empty || Message is null || Message.ExecutionId == Guid.Empty || Message.MessageId == Guid.Empty
        || !Enum.IsDefined(Message.Role) || Message.HistoryRevision < 0)
      throw new ArgumentException("The markdown key has an invalid message identity.", nameof(Message));
    if (FromSegment < 0 || ThroughSegment <= FromSegment || SourceUtf8Bytes < 0)
      throw new ArgumentException("The markdown key has an invalid source extent or byte count.", nameof(ThroughSegment));
    _ = CheckedSubtract(ThroughSegment, FromSegment, nameof(ThroughSegment));
    if (FinalOutputCommitted && !IsClosed)
      throw new ArgumentException("Committed final output must be closed.", nameof(FinalOutputCommitted));
    if (Pipeline != MarkdownRenderIndex.PipelineVersion)
      throw new ArgumentException("The markdown key has an unsupported pipeline.", nameof(Pipeline));
  }

  // Segment extents have one byte count, irrespective of closure facts. Appends
  // can add empty segments but cannot remove bytes from this immutable start.
  internal void ValidateSize(MessageMarkdownKey other) {
    if (SessionId != other.SessionId || Message != other.Message || FromSegment != other.FromSegment || Pipeline != other.Pipeline) return;
    if ((ThroughSegment == other.ThroughSegment && SourceUtf8Bytes != other.SourceUtf8Bytes)
        || (ThroughSegment > other.ThroughSegment && SourceUtf8Bytes < other.SourceUtf8Bytes)
        || (ThroughSegment < other.ThroughSegment && SourceUtf8Bytes > other.SourceUtf8Bytes))
      throw new ArgumentException("The Markdown extent contradicts a retained source byte count.");
  }

  internal bool Matches(MessageMarkdownSource source) {
    int actualSourceUtf8Bytes = Encoding.UTF8.GetByteCount(source.Source);
    return SessionId == source.SessionId && Message == source.Message && IsClosed == source.IsClosed
        && source.From.Message == Message && source.AvailableThrough.Message == Message
        && FromSegment == source.From.SegmentIndex && source.From.Utf8Offset == 0
        && ThroughSegment == source.AvailableThrough.SegmentIndex && source.AvailableThrough.Utf8Offset == 0
        && SourceUtf8Bytes == source.Utf8Bytes && SourceUtf8Bytes == actualSourceUtf8Bytes
        && FinalOutputCommitted == source.FinalOutputCommitted && Pipeline == MarkdownRenderIndex.PipelineVersion;
  }

  static ArgumentException InvalidEntry(string parameterName, string message) => new(message, parameterName);

  static long CheckedAdd(long left, long right, string parameterName) {
    try {
      return checked(left + right);
    } catch (OverflowException error) {
      throw new ArgumentException("The positioned entry exceeds supported range arithmetic.", parameterName, error);
    }
  }

  static long CheckedSubtract(long left, long right, string parameterName) {
    try {
      return checked(left - right);
    } catch (OverflowException error) {
      throw new ArgumentException("The positioned entry exceeds supported range arithmetic.", parameterName, error);
    }
  }
}

// Limits include stale pinned generations and independent interests/leases.
// Transient source, parser trees and unpublished indices on the two workers are separate.
internal sealed record MessageMarkdownCacheLimits(
    int Entries, long RetainedBytes, int PendingKeys, int Leases, int SmallMessageUtf8Bytes) {
  internal void Validate() {
    ArgumentOutOfRangeException.ThrowIfLessThan(Entries, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(RetainedBytes, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(PendingKeys, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(Leases, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(SmallMessageUtf8Bytes, 1);
  }
}

internal sealed class MarkdownCacheFullException() : InvalidOperationException("The Markdown cache is pinned or its lease limit is full. Release unused leases and retry; keep the previous formatted Markdown view.");
internal sealed class MarkdownQueueFullException() : InvalidOperationException("The Markdown queue is full.");
internal sealed class MarkdownSupersededException() : InvalidOperationException("A newer pending Markdown request replaced this extent.");
internal sealed class MarkdownUnavailableException() : InvalidOperationException("The Markdown generation is no longer available. Rebuild from a source anchor.");
internal sealed class MarkdownTooLargeException() : InvalidOperationException("The derived Markdown render index exceeds the cache byte limit.");
