using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// Argument validation and failure settlement for web_read; content settlement lives in WebReadOutputTests.
public sealed class WebReadToolTests {
  [Fact]
  public async Task NonPublicUrlsAreRejectedWithoutContactingTheBrowser() {
    var tool = new WebReadTool(new WebBrowser());
    foreach (string url in new[] { "file:///c:/data.txt", "ftp://example.com/file", "not a url", "example.com/page" }) {
      ToolOutput result = await Run(tool, new { url });
      Assert.True(result.IsError);
      Assert.Equal("web_read failed: url must be an absolute http(s) address.", result.Text);
    }
    ToolOutput credentialled = await Run(tool, new { url = "https://user@example.com/private" });
    Assert.True(credentialled.IsError);
    Assert.Equal("web_read failed: url must not carry credentials.", credentialled.Text);
  }

  [Fact]
  public async Task MissingUrlArgumentFailsWithoutContactingTheBrowser() {
    var tool = new WebReadTool(new WebBrowser());
    ToolOutput result = await Run(tool, new { });
    Assert.True(result.IsError);
    Assert.StartsWith("web_read failed:", result.Text, StringComparison.Ordinal);
    Assert.NotEqual("web_read failed: url must be an absolute http(s) address.", result.Text);
  }

  [Fact]
  public void FailuresSettleTimeLimitsCancellationAndTransportProblems() {
    ToolOutput timedOut = WebReadTool.Fault(new TimeoutException(), CancellationToken.None);
    Assert.True(timedOut.IsError);
    Assert.StartsWith("web_read timed out:", timedOut.Text, StringComparison.Ordinal);

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert.Equal("web_read cancelled.", WebReadTool.Fault(new OperationCanceledException(), cancelled.Token).Text);
    Assert.Equal("web_read cancelled.", WebReadTool.Fault(new OperationCanceledException(), CancellationToken.None).Text);

    Assert.Equal("web_read timed out: Timed out waiting for a browser slot.",
      WebReadTool.Fault(new TimeoutException("Timed out waiting for a browser slot."), CancellationToken.None).Text);
    ToolOutput failed = WebReadTool.Fault(new InvalidOperationException("boom"), CancellationToken.None);
    Assert.True(failed.IsError);
    Assert.Equal("web_read failed: boom", failed.Text);
  }

  [Fact]
  public void DescriptionDisclosesBrowserRenderingAndSpillBehaviour() {
    var tool = new WebReadTool(new WebBrowser());
    Assert.Equal("web_read", tool.Name);
    Assert.Contains("sandboxed browser session", tool.Description, StringComparison.Ordinal);
    Assert.Contains("temp file", tool.Description, StringComparison.Ordinal);
    JsonElement schema = tool.ParametersSchema;
    Assert.True(schema.GetProperty("properties").GetProperty("url").GetProperty("type").GetString() == "string");
    var required = schema.GetProperty("required");
    Assert.Equal("url", required.EnumerateArray().Single().GetString());
  }

  private static async Task<ToolOutput> Run(AgentTool tool, object arguments) =>
    await tool.RunAsync(JsonSerializer.SerializeToElement(arguments),
      new ToolContext(".", Guid.NewGuid(), "web-read-test", false, _ => { }), CancellationToken.None);
}
