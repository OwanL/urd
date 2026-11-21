using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Tools;

public sealed class PlaywrightSignInTests {
  private const string Origin = "https://signin.example.test";

  [Fact]
  public async Task OpenAndContinueRequireTheCurrentVisiblePrompt() {
    await using var fixture = new SignInFixture();
    Task<bool> run = fixture.Run();
    BrowserSignInView requested = Assert.IsType<BrowserSignInView>(await fixture.Read());
    Assert.Equal(Origin, requested.Origin);
    Assert.Equal(BrowserSignInStage.Requested, requested.Stage);
    Assert.False(await fixture.Act(requested.Id, "continue"));
    Assert.False(fixture.Window.Entered.Task.IsCompleted);
    Assert.True(await fixture.Act(requested.Id, "open"));
    Assert.False(await fixture.Act(requested.Id, "open"));
    await fixture.Window.Entered.Task.WaitAsync(TestTimeout);
    Assert.Equal(BrowserSignInStage.Opening, (await fixture.Read())!.Stage);
    Assert.False(await fixture.Act(requested.Id, "continue"));

    await fixture.Window.MarkVisible!();
    Assert.Equal(BrowserSignInStage.Visible, (await fixture.Read())!.Stage);
    Assert.False(run.IsCompleted);
    Assert.False(fixture.Window.Continue!.IsCompleted);
    Assert.True(await fixture.Act(requested.Id, "continue"));
    await fixture.Window.Continue.WaitAsync(TestTimeout);
    Assert.Null(await fixture.Read());
    fixture.Window.Result.TrySetResult(true);
    Assert.True(await run.WaitAsync(TestTimeout));
    Assert.False(await fixture.Act(requested.Id, "continue"));
  }

  [Fact]
  public async Task ClosedWindowCannotSucceedWithoutContinueOrResurrect() {
    await using var fixture = new SignInFixture();
    Task<bool> run = fixture.Run();
    BrowserSignInView prompt = Assert.IsType<BrowserSignInView>(await fixture.Read());
    Assert.True(await fixture.Act(prompt.Id, "open"));
    await fixture.Window.Entered.Task.WaitAsync(TestTimeout);
    await fixture.Window.MarkVisible!();
    fixture.Window.Result.TrySetResult(true);
    Assert.False(await run.WaitAsync(TestTimeout));
    Assert.Null(await fixture.Read());
    Assert.False(await fixture.Act(prompt.Id, "continue"));
    await fixture.Window.MarkVisible();
    Assert.Null(await fixture.Read());
  }

  [Fact]
  public async Task CancelledPromptCannotAffectANewPrompt() {
    await using var fixture = new SignInFixture();
    Task<bool> first = fixture.Run();
    BrowserSignInView old = Assert.IsType<BrowserSignInView>(await fixture.Read());
    Assert.True(await fixture.Act(old.Id, "open"));
    await fixture.Window.Entered.Task.WaitAsync(TestTimeout);
    Assert.True(await fixture.Act(old.Id, "cancel"));
    Assert.False(await first.WaitAsync(TestTimeout));
    Assert.Null(await fixture.Read());
    Func<Task> oldVisible = fixture.Window.MarkVisible!;

    var nextWindow = new FakeWindow();
    Task<bool> second = fixture.Run(nextWindow);
    BrowserSignInView current = Assert.IsType<BrowserSignInView>(await fixture.Read());
    Assert.NotEqual(old.Id, current.Id);
    await oldVisible();
    Assert.Equal(current, await fixture.Read());
    Assert.False(await fixture.Act(old.Id, "continue"));
    Assert.False(await fixture.Act(old.Id, "cancel"));
    Assert.True(await fixture.Act(current.Id, "open"));
    await nextWindow.Entered.Task.WaitAsync(TestTimeout);
    await nextWindow.MarkVisible!();
    Assert.False(await fixture.Act(old.Id, "continue"));
    Assert.False(nextWindow.Continue!.IsCompleted);
    Assert.True(await fixture.Act(current.Id, "cancel"));
    Assert.False(await second.WaitAsync(TestTimeout));
    await nextWindow.MarkVisible();
    Assert.Null(await fixture.Read());
  }

  [Fact]
  public async Task CancellationBeforeOpenRemovesPrompt() {
    await using var fixture = new SignInFixture();
    using var cancellation = new CancellationTokenSource();
    Task<bool> run = fixture.Run(cancellation: cancellation.Token);
    BrowserSignInView prompt = Assert.IsType<BrowserSignInView>(await fixture.Read());
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TestTimeout));
    Assert.Null(await fixture.Read());
    Assert.False(await fixture.Act(prompt.Id, "open"));
    Assert.False(fixture.Window.Entered.Task.IsCompleted);
  }

  private sealed class FakeWindow {
    internal TaskCompletionSource Entered { get; } = Signal();
    internal TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? Continue { get; private set; }
    internal Func<Task>? MarkVisible { get; private set; }

    internal async Task<bool> Open(Task continuation, Func<Task> markVisible, CancellationToken cancellation) {
      Continue = continuation;
      MarkVisible = markVisible;
      Entered.TrySetResult();
      return await Result.Task.WaitAsync(cancellation);
    }
  }

  private sealed class SignInFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly UrdStore store;
    private readonly PrimarySession session;
    private readonly CancellationTokenSource shutdown = new();

    internal SignInFixture() {
      store = UrdStore.Open(database.Path);
      session = new(store.Sessions.CreatePrimary(Guid.NewGuid()), store,
          _ => throw new InvalidOperationException("No chat client is needed."));
    }

    internal FakeWindow Window { get; } = new();

    internal Task<bool> Run(FakeWindow? window = null, CancellationToken cancellation = default) =>
        session.RunBrowserSignInAsync(Origin, (window ?? Window).Open,
            cancellation.CanBeCanceled ? cancellation : shutdown.Token);

    internal Task<BrowserSignInView?> Read() => session.ReadBrowserSignInAsync(CancellationToken.None);
    internal Task<bool> Act(Guid id, string action) => session.ActOnBrowserSignInAsync(id, action, CancellationToken.None);

    public async ValueTask DisposeAsync() {
      await shutdown.CancelAsync();
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout, CancellationToken.None);
      shutdown.Dispose();
      store.Dispose();
      database.Dispose();
    }
  }
}
