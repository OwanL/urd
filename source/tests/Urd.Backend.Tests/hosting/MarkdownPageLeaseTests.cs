using Urd.Backend.Tests.Support;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class MarkdownPageLeaseTests {
  [Fact]
  public async Task SameGenerationLeasesHaveIndependentPageTokensAndOwnership() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new FakeChatClient();
    (PrimarySession session, TranscriptEntryRef entry) = await CreateEntryAsync(store, client);
    await using var service = CreateService();
    using var page = new SessionEventPage(Guid.NewGuid(), _ => { });
    Guid firstToken = Guid.NewGuid();
    Guid secondToken = Guid.NewGuid();

    MessageMarkdownLease first = await page.MarkdownLeases.OpenAsync(firstToken,
        cancellation => service.OpenAsync(session, entry, cancellation), CancellationToken.None);
    MessageMarkdownLease second = await page.MarkdownLeases.OpenAsync(secondToken,
        cancellation => service.OpenAsync(session, entry, cancellation), CancellationToken.None);

    Assert.Equal(first.Manifest.Generation, second.Manifest.Generation);
    await Assert.ThrowsAsync<MarkdownPageLeases.DuplicateTokenException>(() => page.MarkdownLeases.OpenAsync(
        firstToken, _ => throw new InvalidOperationException("Duplicate tokens must not start another open."),
        CancellationToken.None));
    Assert.Equal("independent lease", ReadText(page, firstToken));
    Assert.Equal("independent lease", ReadText(page, secondToken));

    page.MarkdownLeases.Release(firstToken);
    page.MarkdownLeases.Release(firstToken);

    Assert.Throws<MarkdownUnavailableException>(() => ReadText(page, firstToken));
    Assert.Throws<MarkdownUnavailableException>(() => first.ReadPieces(first.Locate(0)));
    Assert.Equal("independent lease", ReadText(page, secondToken));

    page.Close();
    Assert.Throws<MarkdownUnavailableException>(() => second.ReadPieces(second.Locate(0)));
    Assert.Throws<MarkdownUnavailableException>(() => ReadText(page, secondToken));
  }

  [Theory]
  [InlineData(PendingAbort.Release)]
  [InlineData(PendingAbort.Close)]
  [InlineData(PendingAbort.RequestCancellation)]
  public async Task PendingDeliveryAfterAbortIsCancelledAndDisposed(PendingAbort abort) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new FakeChatClient();
    (PrimarySession session, TranscriptEntryRef entry) = await CreateEntryAsync(store, client);
    await using var service = CreateService();
    using var page = new SessionEventPage(Guid.NewGuid(), _ => { });
    using var requestCancellation = new CancellationTokenSource();
    MessageMarkdownLease lateLease = await service.OpenAsync(session, entry);
    var openerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var delivery = new TaskCompletionSource<MessageMarkdownLease>();
    Guid token = Guid.NewGuid();

    Task<MessageMarkdownLease> opening = page.MarkdownLeases.OpenAsync(token, cancellation => {
      cancellation.Register(() => cancellationObserved.TrySetResult());
      openerStarted.TrySetResult();
      return delivery.Task;
    }, requestCancellation.Token);

    await openerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    switch (abort) {
      case PendingAbort.Release:
        page.MarkdownLeases.Release(token);
        break;
      case PendingAbort.Close:
        page.Close();
        break;
      case PendingAbort.RequestCancellation:
        await requestCancellation.CancelAsync();
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(abort));
    }

    await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.False(opening.IsCompleted);
    delivery.SetResult(lateLease);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
    Assert.Throws<MarkdownUnavailableException>(() => lateLease.ReadPieces(lateLease.Locate(0)));
    Assert.Throws<MarkdownUnavailableException>(() => ReadText(page, token));
  }

  private static string ReadText(SessionEventPage page, Guid token) => string.Concat(
      page.MarkdownLeases.Read(token, lease => lease.ReadPieces(lease.Locate(0))).Pieces
          .Select(piece => piece.Text));

  private static MessageMarkdownService CreateService() =>
      new(new(64, 64 * 1024 * 1024, 32, 64, 1024));

  private static async Task<(PrimarySession Session, TranscriptEntryRef Entry)> CreateEntryAsync(
      UrdStore store, FakeChatClient client) {
    const string text = "independent lease";
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid executionId = Guid.NewGuid();
    Guid submissionId = Guid.NewGuid();
    Assert.True(store.UserMessages.Admit(sessionId, 0,
        new(submissionId, 0, text, TestLlmSelection.Default), true, executionId).NewlyAccepted);
    var request = store.Executions.ClaimNextRequest(sessionId, executionId, 0)!;
    Assert.True(store.Executions.StartRequest(sessionId, executionId, 0, request.RequestId));

    TranscriptEntryRef entry = store.Positions.Locate(sessionId, 0, 0).Entries.Single().Descriptor.Entry;
    var sessions = new SessionManager(store, _ => client);
    PrimarySession session = await sessions.GetPrimaryAsync(sessionId);
    return (session, entry);
  }

  public enum PendingAbort {
    Release,
    Close,
    RequestCancellation
  }
}
