using Microsoft.Extensions.AI;
using Urd.Core.MarkdownRendering;
using Urd.Core.Storage;
using Urd.Core.Concurrency;

namespace Urd.Core.Sessions;

// Bounded transcript reads share the session lock without starting or stopping execution.
public sealed class SessionTranscript {
  private readonly Guid sessionId;
  private readonly UrdStore store;
  private readonly AsyncLock sessionLock;

  internal SessionTranscript(Guid sessionId, UrdStore store, AsyncLock sessionLock) {
    this.sessionId = sessionId;
    this.store = store;
    this.sessionLock = sessionLock;
  }

  public Task<MessageContentPage> ReadContentAsync(
      MessageContentCursor cursor, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Content.Read(sessionId, cursor), cancellation);

  public async Task<DataContent?> ReadMarkdownImageAsync(MessageRef message, string key,
      CancellationToken cancellation = default) {
    if (message.Role != MessageRole.Assistant || !MarkdownImageTarget.IsKey(key))
      throw new ArgumentException("A managed image requires an assistant message and a valid image key.");
    cancellation.ThrowIfCancellationRequested();
    Task pending = store.MarkdownImageImports.WaitForPendingAsync(sessionId, message.ExecutionId, message.MessageId, cancellation);
    bool alreadyProcessed = pending.IsCompletedSuccessfully;
    DataContent? captured = store.MarkdownImageFiles.Read(sessionId, message, key);
    if (captured is not null || alreadyProcessed)
      return captured;

    await pending;
    return store.MarkdownImageFiles.Read(sessionId, message, key);
  }

  internal Task<MessageContentExtent> CaptureContentExtentAsync(
      MessageRef message, TranscriptTextFacts expected, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Content.CaptureSourceExtent(sessionId, message, expected), cancellation);

  // Each bounded storage read releases its database lock before caller-side assembly.
  internal MessageContentSegment ReadContentSegment(MessageContentExtent extent, MessageContentCursor cursor) {
    ValidateExtentSession(extent);
    return store.Content.ReadSourceSegment(extent, cursor);
  }

  internal async Task ValidateContentExtentAsync(
      MessageContentExtent extent, CancellationToken cancellation = default) {
    ValidateExtentSession(extent);
    using var _ = await sessionLock.AcquireAsync(cancellation);
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

    return await ReadUnderLockAsync(() => store.Tools.ReadTranscript(
        sessionId, expectedHistoryRevision, requestId, callId, returned, characterLimit), cancellation);
  }

  public Task<TranscriptHistoryPage> ReadHistoryAsync(
      TranscriptHistoryQuery query, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.History.Read(sessionId, query), cancellation);

  public Task<TranscriptPositionState> ReadPositionSummaryAsync(
      long expectedHistoryRevision, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Positions.Summary(sessionId, expectedHistoryRevision), cancellation);

  public Task<TranscriptPositionWindow> LocatePositionAsync(
      long expectedHistoryRevision, double fraction, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Positions.LocateFraction(sessionId, expectedHistoryRevision, fraction), cancellation);

  public Task<TranscriptPromptPage> ReadPromptPageAsync(
      long expectedHistoryRevision, long? afterRank = null, int limit = 60,
      CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Positions.Prompts(sessionId, expectedHistoryRevision, afterRank, limit), cancellation);

  public Task<TranscriptPositionWindow> ActivatePromptAsync(
      long expectedHistoryRevision, TranscriptEntryRef prompt, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Positions.ActivatePrompt(sessionId, expectedHistoryRevision, prompt), cancellation);

  public Task<TranscriptPositionWindow> ReadEntryPositionAsync(
      long expectedHistoryRevision, TranscriptEntryRef entry, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Positions.LocateEntry(sessionId, expectedHistoryRevision, entry), cancellation);

  private async Task<T> ReadUnderLockAsync<T>(Func<T> read, CancellationToken cancellation) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    return read();
  }
}
