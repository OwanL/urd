using Urd.Core.Sessions;

namespace Urd.Core.Display;

// A lightweight immutable snapshot, never an assembled source. Captures use
// committed segment boundaries, including while the message is still open.
internal sealed record MessageDisplayKey(
    Guid SessionId, MessageRef Message, long FromSegment, long ThroughSegment,
    bool IsClosed, bool FinalOutputCommitted, string Pipeline = MessageDisplayIndex.PipelineVersion) {
  internal TranscriptTextFacts TextFacts => new(
      new(Message, FromSegment, 0), new(Message, ThroughSegment, 0), IsClosed, FinalOutputCommitted);

  internal bool Matches(MessageDisplaySource source) =>
      SessionId == source.SessionId && Message == source.Message && IsClosed == source.IsClosed
      && source.From.Message == Message && source.AvailableThrough.Message == Message
      && FromSegment == source.From.SegmentIndex && source.From.Utf8Offset == 0
      && ThroughSegment == source.AvailableThrough.SegmentIndex && source.AvailableThrough.Utf8Offset == 0
      && FinalOutputCommitted == source.FinalOutputCommitted && Pipeline == MessageDisplayIndex.PipelineVersion;
}

// Limits include stale pinned generations and independent interests/leases.
internal sealed record MessageDisplayCacheLimits(int Entries, long RetainedBytes, int PendingKeys, int Leases) {
  internal void Validate() {
    ArgumentOutOfRangeException.ThrowIfLessThan(Entries, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(RetainedBytes, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(PendingKeys, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(Leases, 1);
  }
}

internal sealed class DisplayCacheFullException() : InvalidOperationException("The display cache is pinned or its lease limit is full. Release unused leases and retry; keep the previous formatted view.");
internal sealed class DisplayQueueFullException() : InvalidOperationException("The display queue is full.");
internal sealed class DisplaySupersededException() : InvalidOperationException("A newer pending display request replaced this extent.");
internal sealed class DisplayUnavailableException() : InvalidOperationException("The display generation is no longer available. Rebuild from a source anchor.");
internal sealed class DisplayTooLargeException() : InvalidOperationException("The derived display exceeds the cache byte limit.");
