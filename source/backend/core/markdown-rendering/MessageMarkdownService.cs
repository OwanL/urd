using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

public sealed class MessageMarkdownService : IAsyncDisposable {
  readonly MessageMarkdownScheduler scheduler;

  public MessageMarkdownService(MessageMarkdownCacheLimits limits) => scheduler = new(limits);

  public async Task<MessageMarkdownLease> OpenAsync(SessionBase session, TranscriptEntryRef entry,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(entry);

    TranscriptPositionWindow window = await session.Transcript.ReadEntryPositionAsync(
        entry.HistoryRevision, entry, cancellation);
    TranscriptPositionedEntry positioned = window.Entries.SingleOrDefault(candidate =>
        candidate.Descriptor.Entry == entry && candidate.Position.Entry == entry)
        ?? throw new KeyNotFoundException("The entry has no authoritative transcript position.");
    MessageMarkdownKey key = MessageMarkdownKey.FromPositionedEntry(session.Id, positioned);
    MessageMarkdownLease lease = await scheduler.RequestAsync(key, session, cancellation);

    try {
      await session.Transcript.ReadPositionSummaryAsync(entry.HistoryRevision, cancellation);
    } catch (Exception failure) {
      lease.Dispose();
      if (failure is HistoryRevisionConflictException or KeyNotFoundException) {
        try {
          scheduler.Invalidate(session.Id);
        } catch (ObjectDisposedException) {
          // Disposal already clears the cache and prevents further requests.
        }
      }
      throw;
    }

    return lease;
  }

  public void Invalidate(Guid sessionId) => scheduler.Invalidate(sessionId);

  public ValueTask DisposeAsync() => scheduler.DisposeAsync();
}
