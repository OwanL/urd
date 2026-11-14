using Urd.Core.Storage;
using Urd.Core.Concurrency;

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

  internal async Task<MessageContentExtent> CaptureContentExtentAsync(
      MessageRef message, TranscriptTextFacts expected, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Content.CaptureSourceExtent(sessionId, message, expected);
  }

  // Each bounded storage read releases its database lock before caller-side assembly.
  internal MessageContentSegment ReadContentSegment(MessageContentExtent extent, MessageContentCursor cursor) {
    ValidateExtentSession(extent);
    return store.Content.ReadSourceSegment(extent, cursor);
  }

  internal async Task ValidateContentExtentAsync(
      MessageContentExtent extent, CancellationToken cancellation = default) {
    ValidateExtentSession(extent);
    using var held = await gate.AcquireAsync(cancellation);
    store.Content.ValidateSourceExtent(extent);
  }

  private void ValidateExtentSession(MessageContentExtent extent) {
    if (extent.SessionId != sessionId)
      throw new ArgumentException("The content extent belongs to another session.", nameof(extent));
  }

  public async Task<ToolCallTranscript?> ReadToolCallTranscriptAsync(
      long expectedHistoryRevision, Guid requestId, string callId, bool returned,
      int? characterLimit = null, CancellationToken cancellation = default) {
    ArgumentOutOfRangeException.ThrowIfNegative(expectedHistoryRevision);
    if (requestId == Guid.Empty)
      throw new ArgumentException("A request identity is required.", nameof(requestId));
    ArgumentException.ThrowIfNullOrWhiteSpace(callId);
    if (characterLimit is < 0)
      throw new ArgumentOutOfRangeException(nameof(characterLimit));

    using var held = await gate.AcquireAsync(cancellation);
    return store.Tools.ReadTranscript(
        sessionId, expectedHistoryRevision, requestId, callId, returned, characterLimit);
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
