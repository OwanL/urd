namespace Urd.Core.Tools;

// Selection metadata, separate from the instructions sent to the agent.
internal abstract record ToolRelevance {
  private ToolRelevance() { }

  public static ToolRelevance Always { get; } = new AlwaysRelevant();
  private sealed record AlwaysRelevant : ToolRelevance;

}
