using System.Text.Json;
using Urd.Core.Sessions;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ListAgentsToolTests {
  [Fact]
  public async Task MarksPlaceholdersButNotFinalTitlesWithTheSameText() {
    var tool = new ListAgentsTool();
    var context = new HarnessToolContext(Guid.NewGuid(), Guid.NewGuid(), "list", new Listing([
      new(new("new session", false), "idle", null),
      new(new("new session (2)", true), "active", new("new session", false)),
      new(new("generating title", true), "active", new("new session (2)", true))
    ]));

    ToolOutput output = await tool.RunAsync(JsonSerializer.SerializeToElement(new { }), context, CancellationToken.None);
    Assert.Equal("title: new session | state: idle\n"
        + "title: new session (2) [not addressable] | state: active | parent: new session\n"
        + "title: generating title [not addressable] | state: active | parent: new session (2) [not addressable]", output.Text);
    Assert.False(output.IsError);
    Assert.Empty(tool.ParametersSchema.GetProperty("properties").EnumerateObject());
    Assert.False(tool.ParametersSchema.GetProperty("additionalProperties").GetBoolean());
  }

  private sealed class Listing(IReadOnlyList<LiveAgent> agents) : IHarnessSessionServices {
    public SessionRecord? Read(Guid sessionId) => throw new NotSupportedException();
    public Task<IReadOnlyList<LiveAgent>> ListLiveAgentsAsync(CancellationToken cancellation) => Task.FromResult(agents);
    public Task<SubagentRunResult> RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
        CancellationToken cancellation) => throw new NotSupportedException();
    public Task<AgentQuestionResult> AskAgentAsync(HarnessToolContext caller, string recipient, string prompt,
        bool wake, CancellationToken cancellation) => throw new NotSupportedException();
    public bool AnswerAgent(HarnessToolContext caller, string answer) => throw new NotSupportedException();
  }
}
