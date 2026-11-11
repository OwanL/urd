namespace Urd.Core.Context;

// The delegated assignment, including assignments given to primary sessions.
internal sealed class AssignmentGuidance : ContextGuidance {
  public override int Order => 300;

  public override bool AppliesTo(StartingContextInput input) =>
      !string.IsNullOrWhiteSpace(input.AssignmentContext);

  public override string? GetText(StartingContextInput input) =>
      $"Assignment context:\n{input.AssignmentContext}";
}
