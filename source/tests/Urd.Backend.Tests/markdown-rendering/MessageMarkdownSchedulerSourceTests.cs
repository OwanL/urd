using Urd.Backend.Tests.Support;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MessageMarkdownSchedulerSourceTests {
  [Fact]
  public async Task DerivesCommonMessageFromClosedSessionSourceAndKeepsSourceAnchors() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    const string text = "# café **bold**\n\n[late][ref]\n\n[ref]: https://example.test/é\n";
    var key = Admit(store, text);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Markdown reads must not start execution."));
    var manager = new SessionManager(store, _ => client);
    var session = await manager.GetPrimaryAsync(key.SessionId);
    await using var scheduler = CreateScheduler();

    using var ready = await scheduler.RequestAsync(key, session);
    var page = scheduler.Read(ready, scheduler.Locate(ready, 0));

    Assert.Equal("café boldlate", string.Concat(page.Pieces.Select(piece => piece.Text)));
    Assert.Contains(page.Pieces, piece => scheduler.ReadContext(ready, piece.Context,
        new(ready.Generation, 0)).Context.Kind == MarkdownElementKind.Strong);
    var link = Assert.Single(page.Pieces, piece => piece.Text == "late");
    var context = scheduler.ReadContext(ready, link.Context, new(ready.Generation, 0));
    Assert.Equal(MarkdownElementKind.Link, context.Context.Kind);
    Assert.Equal("https://example.test/é", Assert.Single(context.Values, value => value.Kind == MarkdownValueKind.Destination).Text);
    Assert.Equal(new(key.Message, 0, link.Source.Start), scheduler.SourceCursor(ready, link.Source.Start));
    Assert.Equal(new(key.Message, 1, 0), scheduler.SourceCursor(ready, ready.SourceUtf8Bytes));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task PublicServiceUsesPositionedSourceAndReleasesItsLease() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    const string text = "# café **bold**\n\n[late][ref]\n\n[ref]: https://example.test/é\n";
    var key = Admit(store, text);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Markdown reads must not start execution."));
    var manager = new SessionManager(store, _ => client);
    var session = await manager.GetPrimaryAsync(key.SessionId);
    var entry = new TranscriptEntryRef(key.Message.ExecutionId, key.Message.MessageId,
        key.Message.Role == MessageRole.User ? TranscriptEntryKind.User : TranscriptEntryKind.Text,
        key.Message.HistoryRevision);
    await using var service = new MessageMarkdownService(new(64, 64 * 1024 * 1024, 32, 64, 1024));

    await Assert.ThrowsAsync<KeyNotFoundException>(() => service.OpenAsync(session, entry with { EntryId = Guid.NewGuid() }));
    var lease = await service.OpenAsync(session, entry);
    var cursor = lease.Locate(0);
    Assert.Equal(key.TextFacts, lease.Source);
    Assert.Equal("café boldlate", string.Concat(lease.ReadPieces(cursor).Pieces.Select(piece => piece.Text)));
    Assert.Equal(0, client.Calls);

    await service.DisposeAsync();
    Assert.Equal("café boldlate", string.Concat(lease.ReadPieces(cursor).Pieces.Select(piece => piece.Text)));
    lease.Dispose();
    Assert.Throws<MarkdownUnavailableException>(() => lease.ReadPieces(cursor));
  }

  [Fact]
  public async Task SynchronousBuildDoesNotHoldSessionOrDatabaseGate() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var key = Admit(store, "# captured");
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Markdown reads must not start execution."));
    var manager = new SessionManager(store, _ => client);
    var session = await manager.GetPrimaryAsync(key.SessionId);
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var scheduler = CreateScheduler(build: source => {
      entered.TrySetResult();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the Markdown worker.");
      return MarkdownParser.Parse(source.Source);
    });
    try {
      var request = scheduler.RequestAsync(key, session);
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await session.ReadSnapshotAsync(cancellation.Token);
      var extent = await Task.Run(() => store.Content.CaptureSourceExtent(key.SessionId, key.Message, key.TextFacts))
          .WaitAsync(cancellation.Token);
      Assert.Equal(1, extent.AvailableThrough.SegmentIndex);
      Assert.False(request.IsCompleted);
      release.Set();
      using var ready = await request;
    } finally {
      release.Set();
      await scheduler.DisposeAsync();
    }
  }

  static MessageMarkdownScheduler CreateScheduler(Func<MessageMarkdownSource, MarkdownRenderIndex>? build = null) =>
      new(new(64, 64 * 1024 * 1024, 32, 64, 1024), build);

  static MessageMarkdownKey Admit(UrdStore store, string text) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid(), submission = Guid.NewGuid();
    Assert.True(store.UserMessages.Admit(session, 0,
        new(submission, 0, text, TestLlmSelection.Default), true, execution).NewlyAccepted);
    var request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request.RequestId));
    var positioned = store.Positions.Locate(session, 0, 0).Entries.Single();
    return MessageMarkdownKey.FromPositionedEntry(session, positioned);
  }
}
