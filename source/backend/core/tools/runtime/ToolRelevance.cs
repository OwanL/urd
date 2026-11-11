namespace Urd.Core.Tools;

// Selection metadata, separate from the instructions sent to the agent.
internal abstract record ToolRelevance {
  private ToolRelevance() { }

  public static ToolRelevance Always { get; } = new AlwaysRelevant();
  public static ToolRelevance When(string guidance) => new Conditional(guidance);

  private sealed record AlwaysRelevant : ToolRelevance;

  public sealed record Conditional : ToolRelevance {
    public string Guidance { get; }

    public Conditional(string guidance) {
      ArgumentException.ThrowIfNullOrWhiteSpace(guidance);
      Guidance = guidance;
    }
  }
}
