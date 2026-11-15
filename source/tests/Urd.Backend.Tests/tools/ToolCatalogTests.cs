using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolCatalogTests {
  [Fact]
  public void ToolsReturnsTheFrozenGenericToolList() {
    AgentTool[] configured = [new TestTool("read"), new TestTool("write")];
    var catalog = new ToolCatalog(configured);
    var original = configured.ToArray();
    configured[0] = new TestTool("replacement");

    Assert.Equal(original, catalog.Tools);
    Assert.Throws<NotSupportedException>(() =>
        ((IList<AgentTool>)catalog.Tools)[0] = new TestTool("replacement"));
  }

  [Fact]
  public void DuplicateNamesAreRejectedButCaseDistinctNamesAndEmptyConfigurationAreValid() {
    Assert.Throws<ArgumentException>(() => new ToolCatalog([new TestTool("read"), new TestTool("read")]));
    var caseDistinct = new ToolCatalog([new TestTool("read"), new TestTool("READ")]);
    Assert.Equal(2, caseDistinct.Tools.Count);

    var empty = new ToolCatalog([]);
    Assert.Empty(empty.Tools);
  }

  private sealed class TestTool(string name) : AgentTool {
    public override string Name => name;
    public override string Description => "Catalog fixture";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
    public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      Task.FromResult(new ToolOutput("unused", []));
  }
}
