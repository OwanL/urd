using Urd.Backend.Tests.Support;
using Urd.Core.Display;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Display;

public sealed class MessageDisplaysSourceTests {
  [Fact]
  public async Task DerivesCommonMessageFromClosedSessionSourceAndKeepsSourceAnchors() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    const string text = "# café **bold**\n\n[late][ref]\n\n[ref]: https://example.test/é\n";
    var key = Admit(store, text);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Display reads must not start execution."));
    var manager = new SessionManager(store, _ => client);
    var session = await manager.GetPrimaryAsync(key.SessionId);
    await using var displays = CreateDisplays();

    using var ready = await displays.RequestAsync(key, session);
    var page = displays.Read(ready, displays.Locate(ready, 0));

    Assert.Equal("café boldlate", string.Concat(page.Pieces.Select(piece => piece.Text)));
    Assert.Contains(page.Pieces, piece => displays.ReadContext(ready, piece.Context,
        new(ready.Generation, 0)).Context.Kind == DisplayKind.Strong);
    var link = Assert.Single(page.Pieces, piece => piece.Text == "late");
    var context = displays.ReadContext(ready, link.Context, new(ready.Generation, 0));
    Assert.Equal(DisplayKind.Link, context.Context.Kind);
    Assert.Equal("https://example.test/é", Assert.Single(context.Values, value => value.Kind == DisplayValueKind.Destination).Text);
    Assert.Equal(new(key.Message, 0, link.Source.Start), displays.SourceCursor(ready, link.Source.Start));
    Assert.Equal(new(key.Message, 1, 0), displays.SourceCursor(ready, ready.SourceUtf8Bytes));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task SynchronousBuildDoesNotHoldSessionOrDatabaseGate() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var key = Admit(store, "# captured");
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Display reads must not start execution."));
    var manager = new SessionManager(store, _ => client);
    var session = await manager.GetPrimaryAsync(key.SessionId);
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var displays = CreateDisplays(build: source => {
      entered.TrySetResult();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      return MessageDisplayBuilder.Build(source.Source);
    });
    try {
      var request = displays.RequestAsync(key, session);
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
      await displays.DisposeAsync();
    }
  }

  static MessageDisplays CreateDisplays(Func<MessageDisplaySource, MessageDisplayIndex>? build = null) =>
      new(new(64, 64 * 1024 * 1024, 32, 64), build);

  static MessageDisplayKey Admit(UrdStore store, string text) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid(), submission = Guid.NewGuid();
    Assert.True(store.UserMessages.Admit(session, 0,
        new(submission, 0, text, TestLlmSelection.Default), true, execution).NewlyAccepted);
    var request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request.RequestId));
    return new(session, new(execution, submission, MessageRole.User, 0), 0, 1, true, false);
  }
}
