using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

// A lightweight immutable snapshot, never an assembled source. Captures use
// committed segment boundaries, including while the message is still open.
internal sealed record MessageMarkdownKey(
    Guid SessionId, MessageRef Message, long FromSegment, long ThroughSegment,
    bool IsClosed, bool FinalOutputCommitted, string Pipeline = MarkdownRenderIndex.PipelineVersion) {
  internal TranscriptTextFacts TextFacts => new(
      new(Message, FromSegment, 0), new(Message, ThroughSegment, 0), IsClosed, FinalOutputCommitted);

  internal bool Matches(MessageMarkdownSource source) =>
      SessionId == source.SessionId && Message == source.Message && IsClosed == source.IsClosed
      && source.From.Message == Message && source.AvailableThrough.Message == Message
      && FromSegment == source.From.SegmentIndex && source.From.Utf8Offset == 0
      && ThroughSegment == source.AvailableThrough.SegmentIndex && source.AvailableThrough.Utf8Offset == 0
      && FinalOutputCommitted == source.FinalOutputCommitted && Pipeline == MarkdownRenderIndex.PipelineVersion;
}

// Limits include stale pinned generations and independent interests/leases.
internal sealed record MessageMarkdownCacheLimits(int Entries, long RetainedBytes, int PendingKeys, int Leases) {
  internal void Validate() {
    ArgumentOutOfRangeException.ThrowIfLessThan(Entries, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(RetainedBytes, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(PendingKeys, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(Leases, 1);
  }
}

internal sealed class MarkdownCacheFullException() : InvalidOperationException("The Markdown cache is pinned or its lease limit is full. Release unused leases and retry; keep the previous formatted Markdown view.");
internal sealed class MarkdownQueueFullException() : InvalidOperationException("The Markdown queue is full.");
internal sealed class MarkdownSupersededException() : InvalidOperationException("A newer pending Markdown request replaced this extent.");
internal sealed class MarkdownUnavailableException() : InvalidOperationException("The Markdown generation is no longer available. Rebuild from a source anchor.");
internal sealed class MarkdownTooLargeException() : InvalidOperationException("The derived Markdown render index exceeds the cache byte limit.");
