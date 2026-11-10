using System.Text.Json;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolCatalogTests {
  [Fact]
  public void EveryFamilyGetsAllGenericToolsAndSavedNamesRemainFindable() {
    AgentTool[] configured = [new TestTool("read"), new TestTool("write")];
    var catalog = new ToolCatalog(configured);
    var original = configured.ToArray();
    configured[0] = new TestTool("replacement");

    foreach (var family in Enum.GetValues<ModelFamily>())
      Assert.Equal(original, catalog.For(family));
    Assert.Same(original[0], catalog.Find("read"));
    Assert.Same(original[1], catalog.Find("write"));
    Assert.Null(catalog.Find("READ"));
    Assert.Null(catalog.Find("replacement"));
  }

  [Fact]
  public void DuplicateNamesAreRejectedButEmptyConfigurationIsValid() {
    Assert.Throws<ArgumentException>(() => new ToolCatalog([new TestTool("read"), new TestTool("read")]));
    var empty = new ToolCatalog([]);
    Assert.Empty(empty.For(ModelFamily.Other));
    Assert.Null(empty.Find("read"));
  }

  private sealed class TestTool(string name) : AgentTool {
    public override string Name => name;
    public override string Description => "Catalog fixture";
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
    public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      Task.FromResult(new ToolOutput("unused", []));
  }
}
