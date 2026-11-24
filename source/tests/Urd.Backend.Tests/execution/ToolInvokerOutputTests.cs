using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Execution;

public sealed class ToolInvokerOutputTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task OutputAtEitherLimitRemainsComplete(bool lineLimit) {
    string text = lineLimit ? Lines(2000) : new string('a', 50 * 1024 - 2) + "é";
    LlmToolResult result = await InvokeAsync(new ToolOutput(text, []));

    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    Assert.Equal(text, result.Output.Text);
  }

  [Theory]
  [InlineData(false, false, false)]
  [InlineData(true, false, false)]
  [InlineData(false, true, false)]
  [InlineData(false, false, true)]
  public async Task OversizedOutputSpillsCompleteTextAndPreservesOtherResultData(
      bool lineLimit, bool isError, bool hasImage) {
    string text = lineLimit ? Lines(2001) : new string('a', 50 * 1024) + "é end";
    var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
    JsonElement details = JsonSerializer.SerializeToElement(new { marker = "original metadata", count = 17 });
    LlmToolResult result = await InvokeAsync(new ToolOutput(text, hasImage ? [image] : [], details, isError));

    Assert.Equal(isError ? ExecutionOutcome.Failed : ExecutionOutcome.Completed, result.Outcome);
    Assert.Equal(isError, result.Output.IsError);
    Assert.Equal(hasImage ? 1 : 0, result.Output.Images.Count);
    if (hasImage) {
      Assert.Equal(image.MediaType, result.Output.Images[0].MediaType);
      Assert.Equal(image.Data.ToArray(), result.Output.Images[0].Data.ToArray());
    }
    Assert.Equal("original metadata", result.Output.Details!.Value.GetProperty("marker").GetString());
    Assert.Equal(17, result.Output.Details.Value.GetProperty("count").GetInt32());
    Assert.True(Encoding.UTF8.GetByteCount(result.Output.Text) <= 50 * 1024);
    Assert.True(result.Output.Text.Count(character => character == '\n') <= 2000);
    Assert.NotEqual(text, result.Output.Text);

    string path = FindReferencedTempFile(result.Output.Text);
    try {
      Assert.Equal(text, await File.ReadAllTextAsync(path, Encoding.UTF8));
    } finally {
      File.Delete(path);
    }
  }

  private static string Lines(int count) => string.Join('\n', Enumerable.Range(0, count).Select(index => $"line {index:D4}"));

  private static async Task<LlmToolResult> InvokeAsync(ToolOutput output) {
    var tool = new TestAgentTool("test_output", (_, _, _) => Task.FromResult(output));
    var call = new LlmToolCall("call", tool.Name, JsonSerializer.SerializeToElement(new { }));
    return await ToolInvoker.InvokeAsync(Guid.NewGuid(), call, [tool], [AgentToolDeclaration.Create(tool)],
        Path.GetTempPath(), supportsImages: true, background: false, CancellationToken.None);
  }

  private static string FindReferencedTempFile(string text) {
    string temp = Path.GetTempPath();
    int start = text.IndexOf(temp, StringComparison.OrdinalIgnoreCase);
    Assert.True(start >= 0, "The model text must reference the full output in a temp file.");
    int end = text.IndexOfAny(['\r', '\n'], start);
    if (end < 0)
      end = text.Length;
    // The reference may be followed by punctuation or explanatory text.
    while (end > start) {
      string candidate = text[start..end];
      if (File.Exists(candidate))
        return candidate;
      end--;
    }
    throw new Xunit.Sdk.XunitException("The referenced temp file does not exist.");
  }
}
