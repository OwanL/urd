using System.Text.Json;
using Microsoft.Playwright;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class BrowserNavigationTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task HistoryReplacementStillWaitsForDomContentLoadedWhenTheAbortArrivesFirst(bool waitBeforeAbort) {
    await using var session = new NavigationSession();
    var navigation = new BrowserNavigation();
    await navigation.WatchAsync(session);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    int before = navigation.Version;
    session.Emit("Page.frameStartedNavigating", """
      {"frameId":"main","loaderId":"pending","navigationType":"differentDocument"}
      """);
    Task<NavigationWait>? loading = waitBeforeAbort ? navigation.WaitAfterAsync(before, false, cancellation.Token) : null;

    // history.back() can abort the active request before reporting the history loader.
    session.Emit("Network.loadingFailed", """
      {"requestId":"pending","type":"Document","errorText":"net::ERR_ABORTED"}
      """);
    loading ??= navigation.WaitAfterAsync(before, false, cancellation.Token);
    session.Emit("Page.frameStartedNavigating", """
      {"frameId":"main","loaderId":"history","navigationType":"historyDifferentDocument"}
      """);
    await Assert.ThrowsAsync<TimeoutException>(() => loading.WaitAsync(TimeSpan.FromMilliseconds(150)));

    session.Emit("Page.frameNavigated", """
      {"frame":{"id":"main","loaderId":"history"},"type":"Navigation"}
      """);
    session.Emit("Page.lifecycleEvent", """
      {"frameId":"main","loaderId":"history","name":"DOMContentLoaded"}
      """);
    Assert.Equal(NavigationWait.Ready, await loading);
  }

  private sealed class NavigationSession : ICDPSession {
    private readonly Dictionary<string, NavigationEvent> events = [];

    public event EventHandler<ICDPSession>? Close;

    public ICDPSessionEvent Event(string eventName) => events[eventName] = new(eventName);

    public Task<JsonElement?> SendAsync(string method, Dictionary<string, object>? args = null,
        CDPSessionSendOptions? options = null) =>
        Task.FromResult<JsonElement?>(method == "Page.getFrameTree"
            ? JsonSerializer.Deserialize<JsonElement>("""{"frameTree":{"frame":{"id":"main"}}}""")
            : null);

    public Task DetachAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() {
      Close?.Invoke(this, this);
      return ValueTask.CompletedTask;
    }

    public void Emit(string name, string data) => events[name].Emit(JsonSerializer.Deserialize<JsonElement>(data));
  }

  private sealed class NavigationEvent(string eventName) : ICDPSessionEvent {
    public string EventName => eventName;
    public event EventHandler<JsonElement?>? OnEvent;

    public void Emit(JsonElement data) => OnEvent?.Invoke(this, data);
  }
}
