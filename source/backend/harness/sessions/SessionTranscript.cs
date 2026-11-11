using Urd.Core.MarkdownRendering;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Bounded transcript reads share the session gate without starting or stopping execution.
public sealed class SessionTranscript {
  private readonly Guid sessionId;
  private readonly UrdStore store;
  private readonly AsyncLock gate;

  internal SessionTranscript(Guid sessionId, UrdStore store, AsyncLock gate) {
    this.sessionId = sessionId;
    this.store = store;
    this.gate = gate;
  }

  public async Task<MessageContentPage> ReadContentAsync(
      MessageContentCursor cursor, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Content.Read(sessionId, cursor);
  }

  internal async Task<MessageMarkdownSource> ReadMarkdownSourceAsync(
      MessageMarkdownKey key, CancellationToken cancellation = default) {
    if (key.SessionId != sessionId)
      throw new ArgumentException("The session does not match the markdown key.", nameof(key));
    MessageContentExtent extent;
    using (await gate.AcquireAsync(cancellation))
      extent = store.Content.CaptureSourceExtent(sessionId, key.Message, key.TextFacts);

    MessageMarkdownSource source = MessageMarkdownSourceReader.Read(store.Content, extent, cancellation);

    using (await gate.AcquireAsync(cancellation)) {
      store.Content.ValidateSourceExtent(extent);
      return source;
    }
  }

  public async Task<TranscriptHistoryPage> ReadHistoryAsync(
      TranscriptHistoryQuery query, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.History.Read(sessionId, query);
  }

  public async Task<TranscriptPositionState> ReadPositionSummaryAsync(
      long expectedHistoryRevision, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Positions.Summary(sessionId, expectedHistoryRevision);
  }

  public async Task<TranscriptPositionWindow> LocatePositionAsync(
      long expectedHistoryRevision, double fraction, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Positions.LocateFraction(sessionId, expectedHistoryRevision, fraction);
  }

  public async Task<TranscriptPromptPage> ReadPromptPageAsync(
      long expectedHistoryRevision, long? afterRank = null, int limit = 60,
      CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Positions.Prompts(sessionId, expectedHistoryRevision, afterRank, limit);
  }

  public async Task<TranscriptPositionWindow> ActivatePromptAsync(
      long expectedHistoryRevision, TranscriptEntryRef prompt, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Positions.ActivatePrompt(sessionId, expectedHistoryRevision, prompt);
  }

  public async Task<TranscriptPositionWindow> ReadEntryPositionAsync(
      long expectedHistoryRevision, TranscriptEntryRef entry, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Positions.LocateEntry(sessionId, expectedHistoryRevision, entry);
  }
}
