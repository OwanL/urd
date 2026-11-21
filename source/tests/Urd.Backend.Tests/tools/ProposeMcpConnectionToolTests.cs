using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ProposeMcpConnectionToolTests {
  [Theory]
  [InlineData("https://example.com/mcp")]
  [InlineData("http://localhost:3000/mcp")]
  [InlineData("http://[::1]:3000/mcp")]
  public async Task ReturnsProposalWithoutConnecting(string url) {
    var tool = new ProposeMcpConnectionTool();
    ToolOutput result = await tool.RunAsync(JsonSerializer.SerializeToElement(new { name = "Server", url }), null!, default);
    Assert.False(result.IsError);
    Assert.Equal("Server", result.Details!.Value.GetProperty("name").GetString());
    Assert.Equal(url, result.Details.Value.GetProperty("url").GetString());
  }

  [Theory]
  [InlineData("http://example.com/mcp")]
  [InlineData("https://user:password@example.com/mcp")]
  [InlineData("https://example.com/mcp?token=secret")]
  [InlineData("https://example.com/mcp#fragment")]
  [InlineData("https://example.com/mcp?")]
  [InlineData("file:///path")]
  public async Task RejectsUnsafeAddresses(string url) {
    var tool = new ProposeMcpConnectionTool();
    await Assert.ThrowsAsync<ArgumentException>(() => tool.RunAsync(JsonSerializer.SerializeToElement(new { name = "Server", url }), null!, default));
  }

  [Theory]
  [InlineData("{}")]
  [InlineData("{\"name\":\"Server\",\"url\":42}")]
  [InlineData("{\"name\":\"Server\",\"url\":\"https://example.com\",\"secret\":\"x\"}")]
  [InlineData("{\"name\":\"Server\",\"name\":\"Other\",\"url\":\"https://example.com\"}")]
  public async Task RejectsMalformedArguments(string json) {
    var tool = new ProposeMcpConnectionTool();
    using JsonDocument arguments = JsonDocument.Parse(json);
    await Assert.ThrowsAsync<ArgumentException>(() => tool.RunAsync(arguments.RootElement, null!, default));
  }
}
