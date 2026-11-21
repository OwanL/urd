using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

public sealed class MessageMarkdownService : IAsyncDisposable {
  readonly MessageMarkdownScheduler scheduler;

  public MessageMarkdownService(MessageMarkdownCacheLimits limits) => scheduler = new(limits);

  public Task<MessageMarkdownLease> OpenAsync(SessionBase session, TranscriptEntryRef entry,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(session);
    return OpenAsync(session.Transcript, entry, cancellation);
  }

  public async Task<MessageMarkdownLease> OpenAsync(SessionTranscript transcript, TranscriptEntryRef entry,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(transcript);
    ArgumentNullException.ThrowIfNull(entry);

    TranscriptPositionWindow window = await transcript.ReadEntryPositionAsync(
        entry.HistoryRevision, entry, cancellation);
    TranscriptPositionedEntry positioned = window.Entries.SingleOrDefault(candidate =>
        candidate.Descriptor.Entry == entry && candidate.Position.Entry == entry)
        ?? throw new KeyNotFoundException("The entry has no authoritative transcript position.");
    MessageMarkdownKey key = MessageMarkdownKey.FromPositionedEntry(transcript.SessionId, positioned);
    MessageMarkdownLease lease = await scheduler.RequestAsync(key, transcript, cancellation);

    try {
      await transcript.ReadPositionSummaryAsync(entry.HistoryRevision, cancellation);
    } catch (Exception failure) {
      lease.Dispose();
      if (failure is HistoryRevisionConflictException or KeyNotFoundException) {
        try {
          scheduler.Invalidate(transcript.SessionId);
        } catch (ObjectDisposedException) {
          // Disposal already clears the cache and prevents further requests.
        }
      }
      throw;
    }

    return lease;
  }

  public void Invalidate(Guid sessionId) => scheduler.Invalidate(sessionId);

  public void CloseSession(Guid sessionId) => scheduler.Invalidate(sessionId, revokeLeases: true);

  public ValueTask DisposeAsync() => scheduler.DisposeAsync();
}
