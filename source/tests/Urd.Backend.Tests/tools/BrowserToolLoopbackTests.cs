using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// Real BrowserTool on a real BrowserHost, served pages on 127.0.0.1 and a disposable browser
// profile: ref clicks must act, every action must return the post-action snapshot, screenshot
// coordinates must reject stale or outside viewports even under concurrent resizes, and dialogs
// must be reported instead of hanging the tool call.
public sealed class BrowserToolLoopbackTests {
  private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task RefClickActsAndReturnsSnapshotAfterTheAction() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    ToolOutput navigated = await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/click")));
    Assert.Contains("idle", navigated.Text);
    string @ref = RefFor(navigated.Text, "Act");

    ToolOutput clicked = await sandbox.CallAsync(owner, "click", ("ref", @ref));
    Assert.Contains($"Clicked {@ref}.", clicked.Text);
    Assert.Contains("clicked", clicked.Text);
    Assert.DoesNotContain("idle", clicked.Text);

    ToolOutput snapshot = await sandbox.CallAsync(owner, "snapshot");
    Assert.Contains("clicked", snapshot.Text);
  }

  [Fact]
  public async Task ClickTriggeredDialogIsReportedInsteadOfBlockingFurtherActions() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    ToolOutput navigated = await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/dialog")));
    string @ref = RefFor(navigated.Text, "Warn");

    ToolOutput clicked = await sandbox.CallAsync(owner, "click", ("ref", @ref));
    Assert.Contains("A dialog blocks this page: alert \"blocked\"", clicked.Text);

    ToolOutput refused = await sandbox.CallAsync(owner, "click", ("ref", @ref));
    Assert.DoesNotContain("Clicked", refused.Text);
    Assert.Contains("A dialog blocks this page", refused.Text);

    ToolOutput answered = await sandbox.CallAsync(owner, "dialog", ("dismiss", true));
    Assert.Contains("Dialog dismissed.", answered.Text);
    ToolOutput freed = await sandbox.CallAsync(owner, "evaluate", ("expression", "2 + 3"));
    Assert.Contains("Evaluated: 5", freed.Text);
  }

  [Fact]
  public async Task PromptDialogShowsItsDefaultAndAcceptAnswersWithThePromptText() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    ToolOutput navigated = await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/prompt")));
    string @ref = RefFor(navigated.Text, "Ask");

    ToolOutput clicked = await sandbox.CallAsync(owner, "click", ("ref", @ref));
    Assert.Contains("A dialog blocks this page: prompt \"Name?\" (default \"dflt\")", clicked.Text);

    ToolOutput answered = await sandbox.CallAsync(owner, "dialog", ("accept", true), ("prompt", "Ada"));
    Assert.Contains("Dialog accepted.", answered.Text);
    Assert.Contains("Ada", answered.Text);
  }

  [Fact]
  public async Task NavigateReportsAnOpenPageDialogInsteadOfHanging() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    // The dialog opens while the load the goto is waiting for is still blocked by it.
    ToolOutput navigated = await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/alert-load")));
    Assert.Contains("A dialog blocks this page: alert \"on load\"", navigated.Text);
    Assert.DoesNotContain("arrived", navigated.Text);

    ToolOutput answered = await sandbox.CallAsync(owner, "dialog", ("dismiss", true));
    Assert.Contains("Dialog dismissed.", answered.Text);
    ToolOutput snapshot = await sandbox.CallAsync(owner, "snapshot");
    Assert.Contains("arrived", snapshot.Text);
  }

  [Fact]
  public async Task NavigationBackReportsABeforeunloadDialogInsteadOfHanging() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/click")));
    await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/leaver")));
    // Real activation, without which beforeunload dialogs are not offered.
    await sandbox.CallAsync(owner, "click", ("ref", RefFor((await sandbox.CallAsync(owner, "snapshot")).Text, "Stay")));

    ToolOutput backed = await sandbox.CallAsync(owner, "back");
    // Headless Chromium reports the dialog without its beforeunload message.
    Assert.Contains("A dialog blocks this page: beforeunload", backed.Text);

    ToolOutput answered = await sandbox.CallAsync(owner, "dialog", ("accept", true));
    Assert.Contains("Dialog accepted.", answered.Text);
    (await sandbox.WaitForSnapshotTextAsync(owner, "idle")).Text.Contains("idle");
  }

  [Fact]
  public async Task ClickAtUsesTheLatestScreenshotAndRejectsStaleAndOutsideCoordinates() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/targets")));
    BrowserInteraction interaction = sandbox.InteractionOf(owner);

    InvalidOperationException noBasis = await Assert.ThrowsAsync<InvalidOperationException>(
        () => sandbox.CallAsync(owner, "click_at", ("x", 100), ("y", 50)));
    Assert.Contains("Take a screenshot first", noBasis.Message);

    ToolOutput shot = await sandbox.CallAsync(owner, "screenshot");
    Assert.Contains("Captured 1280x800.", shot.Text);    ToolOutput clicked = await sandbox.CallAsync(owner, "click_at", ("x", 100), ("y", 50));
    Assert.Contains("Clicked at 100,50.", clicked.Text);
    Assert.Equal("A", await sandbox.OutTextAsync(owner));

    await interaction.ResizeAsync(900, 600);
    InvalidOperationException stale = await Assert.ThrowsAsync<InvalidOperationException>(
        () => sandbox.CallAsync(owner, "click_at", ("x", 100), ("y", 50)));
    Assert.Contains("The viewport changed since the screenshot", stale.Message);
    Assert.Equal("A", await sandbox.OutTextAsync(owner));

    ToolOutput fresh = await sandbox.CallAsync(owner, "screenshot");
    Assert.Contains("Captured 900x600.", fresh.Text);
    await sandbox.CallAsync(owner, "click_at", ("x", 100), ("y", 50));
    Assert.Equal("AA", await sandbox.OutTextAsync(owner));
    ArgumentException outside = await Assert.ThrowsAsync<ArgumentException>(
        () => sandbox.CallAsync(owner, "click_at", ("x", 900), ("y", 50)));
    Assert.Contains("inside the 900x600 screenshot", outside.Message);
  }

  [Fact]
  public async Task ClickAtNeverRedirectsAcrossConcurrentResizes() {
    await using Sandbox sandbox = await Sandbox.StartAsync();
    Guid owner = Guid.NewGuid();
    await sandbox.CallAsync(owner, "navigate", ("url", sandbox.Site.Url("/targets")));
    BrowserInteraction interaction = sandbox.InteractionOf(owner);
    string previous = "";
    int landed = 0;
    int stale = 0;
    for (int round = 0; round < 30; round++) {
      await interaction.ResizeAsync(1200, 900);
      await sandbox.CallAsync(owner, "screenshot");
      // Immediate resizes make the click_at lose the epoch race most rounds; delayed resizes
      // let it win the gate, which is exactly when a non-atomic check could redirect the click.
      using CancellationTokenSource resizing = new();
      Task resizer = ResizeLoopAsync(interaction, TimeSpan.FromMilliseconds(round % 2 is 0 ? 0 : 30),
          resizing.Token);
      InvalidOperationException? rejected = null;
      try {
        await sandbox.CallAsync(owner, "click_at", ("x", 1100), ("y", 850));
        landed++;
      } catch (InvalidOperationException error) when (error.Message.Contains("viewport changed")) {
        rejected = error;
        stale++;
      } finally {
        await resizing.CancelAsync();
        await resizer;
      }
      string current = await sandbox.OutTextAsync(owner);
      if (rejected is null)
        Assert.Equal(previous + "B", current);
      else
        Assert.Equal(previous, current);
      previous = current;
    }
    Assert.Equal(30, landed + stale);
    Assert.True(landed >= 10, $"Only {landed} of 30 click_at calls landed; the atomic path was barely exercised.");
  }

  private static async Task ResizeLoopAsync(BrowserInteraction interaction, TimeSpan startDelay,
      CancellationToken cancellation) {
    try {
      if (startDelay > TimeSpan.Zero)
        await Task.Delay(startDelay, cancellation).ConfigureAwait(false);
      while (!cancellation.IsCancellationRequested) {
        await interaction.ResizeAsync(700, 500).ConfigureAwait(false);
        await interaction.ResizeAsync(1200, 900).ConfigureAwait(false);
        await Task.Delay(1, cancellation).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // The round is over.
    }
  }

  private static string RefFor(string snapshot, string label) =>
      snapshot.Split('\n').Select(line => new { Line = line, Match = Regex.Match(line, @"\[ref=([fe\d]+)\]") })
          .Where(entry => entry.Match.Success && entry.Line.Contains(label))
          .Select(entry => entry.Match.Groups[1].Value)
          .FirstOrDefault() ?? throw new InvalidOperationException($"No [ref=] for \"{label}\" in:\n{snapshot}");

  // One browser host with a disposable profile and one loopback page server per test.
  private sealed class Sandbox : IAsyncDisposable {
    private readonly string profileRoot;
    private BrowserHost host = null!;

    private Sandbox(string profileRoot) {
      this.profileRoot = profileRoot;
    }

    public BrowserTool Tool { get; private set; } = null!;
    public LoopbackSite Site { get; private set; } = null!;

    public static async Task<Sandbox> StartAsync() {
      Sandbox sandbox = new(Path.Combine(Path.GetTempPath(), "urd-browser-tests", Guid.NewGuid().ToString("N")));
      Directory.CreateDirectory(sandbox.profileRoot);
      sandbox.Site = new LoopbackSite();
      sandbox.Site.Add("/click", """
        <!doctype html><html><body>
        <button onclick="document.getElementById('out').textContent = 'clicked'">Act</button>
        <div id="out">idle</div>
        </body></html>
        """);
      sandbox.Site.Add("/dialog", """
        <!doctype html><html><body>
        <button onclick="alert('blocked')">Warn</button>
        </body></html>
        """);
      sandbox.Site.Add("/prompt", """
        <!doctype html><html><body>
        <button onclick="document.getElementById('out').textContent = prompt('Name?', 'dflt') || 'none'">Ask</button>
        <div id="out">idle</div>
        </body></html>
        """);
      sandbox.Site.Add("/alert-load", """
        <!doctype html><html><body>
        <script>alert('on load')</script><p>arrived</p>
        </body></html>
        """);
      sandbox.Site.Add("/leaver", """
        <!doctype html><html><body>
        <button id="stay">Stay</button>
        <script>window.onbeforeunload = function () { return 'leave?'; }</script>
        </body></html>
        """);
      sandbox.Site.Add("/targets", """
        <!doctype html><html><body style="margin:0">
        <button style="position:fixed;left:0;top:0;width:200px;height:100px"
            onclick="document.getElementById('out').textContent += 'A'">Origin</button>
        <button style="position:fixed;right:0;bottom:0;width:200px;height:100px"
            onclick="document.getElementById('out').textContent += 'B'">Corner</button>
        <div id="out"></div>
        </body></html>
        """);
      sandbox.host = new BrowserHost(sandbox.profileRoot, _ => Task.FromResult(false));
      sandbox.Tool = new BrowserTool(sandbox.host);
      // Provisioning surfaces here, before any assertion, on the machine's first run.
      Guid warmingOwner = Guid.NewGuid();
      await sandbox.host.OpenAsync(warmingOwner, CancellationToken.None).WaitAsync(StepTimeout);
      await sandbox.host.CloseOwnerAsync(warmingOwner);
      return sandbox;
    }

    public async ValueTask DisposeAsync() {
      await host.DisposeAsync();
      Site.Dispose();
      for (int attempt = 0; attempt < 5; attempt++) {
        try {
          Directory.Delete(profileRoot, recursive: true);
          return;
        } catch (IOException) {
          await Task.Delay(200).ConfigureAwait(false);
        } catch (UnauthorizedAccessException) {
          return;
        }
      }
    }

    public BrowserInteraction InteractionOf(Guid owner) =>
        BrowserInteraction.For(host.Pages.Selected(owner)!.Page);

    public async Task<ToolOutput> CallAsync(Guid owner, string action, params (string Name, JsonNode? Value)[] options) {
      JsonObject values = new() { ["action"] = action };
      foreach ((string name, JsonNode? value) in options)
        values[name] = value;
      using MemoryStream stream = new();
      using (var writer = new Utf8JsonWriter(stream))
        values.WriteTo(writer);
      JsonElement arguments = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
      var context = new HarnessToolContext(owner, Guid.NewGuid(), "call", null!);
      return await Tool.RunAsync(arguments, context, CancellationToken.None).WaitAsync(StepTimeout);
    }

    public async Task<ToolOutput> WaitForSnapshotTextAsync(Guid owner, string expected) {
      for (int attempt = 0; attempt < 20; attempt++) {
        ToolOutput snapshot = await CallAsync(owner, "snapshot");
        if (snapshot.Text.Contains(expected))
          return snapshot;
        await Task.Delay(100).ConfigureAwait(false);
      }
      throw new InvalidOperationException($"The snapshot never showed \"{expected}\".");
    }

    // The click log of the /targets page, read through the tool's own evaluate action.
    public async Task<string> OutTextAsync(Guid owner) {
      ToolOutput result = await CallAsync(owner, "evaluate", ("expression", "document.getElementById('out').textContent"));
      Assert.StartsWith("Evaluated: \"", result.Text);
      return result.Text["Evaluated: ".Length..].Split('\n')[0].Trim('"');
    }
  }

  private sealed class LoopbackSite : IDisposable {
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, string> pages = [];
    private readonly Task serving;

    public LoopbackSite() {
      var probe = new TcpListener(IPAddress.Loopback, 0);
      probe.Start();
      int port = ((IPEndPoint)probe.LocalEndpoint).Port;
      probe.Stop();
      Base = new Uri($"http://127.0.0.1:{port}/");
      listener.Prefixes.Add(Base.GetLeftPart(UriPartial.Authority) + "/");
      listener.Start();
      serving = ServeAsync();
    }

    public Uri Base { get; }

    public string Url(string path) => new Uri(Base, path.TrimStart('/')).AbsoluteUri;

    public void Add(string path, string html) => pages[path] = html;

    private async Task ServeAsync() {
      while (listener.IsListening) {
        HttpListenerContext context;
        try {
          context = await listener.GetContextAsync().ConfigureAwait(false);
        } catch (HttpListenerException) when (!listener.IsListening) {
          break;
        }
        await RespondAsync(context).ConfigureAwait(false);
      }
    }

    private async Task RespondAsync(HttpListenerContext context) {
      try {
        string path = context.Request.Url!.AbsolutePath;
        bool known = pages.TryGetValue(path, out string? html);
        byte[] body = Encoding.UTF8.GetBytes(known ? html! : "not found");
        context.Response.StatusCode = known ? 200 : 404;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
      } finally {
        context.Response.Close();
      }
    }

    public void Dispose() {
      listener.Stop();
      listener.Close();
      GC.KeepAlive(serving);
    }
  }
}
