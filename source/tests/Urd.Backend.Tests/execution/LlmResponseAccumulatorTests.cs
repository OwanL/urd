using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class LlmResponseAccumulatorTests {
  [Fact]
  public async Task NativeReasoningStaysOutOfPublicChunksAndOneFinalEnvelopeIsRetained() {
    var chunks = new List<LlmResponseChunk>();
    var accumulator = new LlmResponseAccumulator(new(Guid.NewGuid(), chunk => {
      chunks.Add(chunk);
      return Task.CompletedTask;
    }));
    await accumulator.AppendAsync(new() { Contents = [new TextReasoningContent("private")] });
    await accumulator.AppendAsync(new() { Contents = [new TextContent("visible")] });
    LlmProviderResponse response = Response();
    var final = new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
    LlmProviderContext.SetResponse(final, response);

    await accumulator.AppendAsync(final);
    await accumulator.FinishAsync();

    Assert.Same(response, accumulator.ProviderResponse);
    Assert.Equal("visible", string.Concat(chunks.Select(chunk => chunk.Text)));
    Assert.True(Assert.Single(chunks).IsFinal);
    Assert.Empty(accumulator.ToolCalls);
    await Assert.ThrowsAsync<InvalidDataException>(() => accumulator.AppendAsync(final));
  }

  [Theory]
  [InlineData("reasoning-without-context")]
  [InlineData("context-without-finish")]
  [InlineData("context-after-finish")]
  [InlineData("update-after-context")]
  [InlineData("exception-after-context")]
  [InlineData("malformed-context")]
  public async Task UnqualifiedOrUnsettledNativeContextCannotSettleSuccessfully(string scenario) {
    var client = new ScriptedChatClient(Stream);
    await using var fixture = new ExecutionFixture(client);
    Guid execution = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(execution).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).Outcome);
    Assert.Equal(0L, fixture.Database.Scalar("SELECT COUNT(*) FROM execution_provider_context;")); // raw-sql: failed request must not retain successful native context
    Assert.Empty(StoredTools.Read(fixture.Database));
    Assert.All(StoredLlmResponses.Read(fixture.Database), chunk => Assert.False(chunk.Final));
    Assert.False((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);

    async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() { Contents = [new TextContent("visible"), new TextReasoningContent("private")] };
      var final = new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
      if (scenario == "context-after-finish")
        yield return final;
      if (scenario != "reasoning-without-context")
        LlmProviderContext.SetResponse(final, scenario == "malformed-context" ? Response() with { Json = "invalid" } : Response());
      if (scenario == "context-without-finish")
        final.FinishReason = null;
      yield return final;
      if (scenario == "update-after-context")
        yield return new() { Contents = [new UsageContent()] };
      if (scenario == "exception-after-context")
        throw new InvalidOperationException("Failure after native completion.");
    }
  }

  private static LlmProviderResponse Response() => new(
      new(Guid.NewGuid(), 0, LlmApi.OpenAIResponses, "fixture-model"),
      """
      {"version":1,"items":[
        {"type":"message","role":"assistant","content":[{"type":"output_text","text":"visible"}]},
        {"type":"reasoning","summary":[{"type":"summary_text","text":"private"}],"encrypted_content":"opaque"}
      ]}
      """);
}
