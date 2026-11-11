namespace Urd.Core.Context;

// Guidance that contributes one system instruction to a starting context.
internal abstract class ContextGuidance {
  public abstract int Order { get; }

  public virtual bool AppliesTo(StartingContextInput input) => true;

  public abstract string? GetText(StartingContextInput input);
}
