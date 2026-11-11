namespace Urd.Core.Context;

// Instructions for sessions carrying out a delegated task.
internal sealed class SubagentGuidance : ContextGuidance {
  public override int Order => 100;

  public override bool AppliesTo(StartingContextInput input) =>
      input.SessionKind == SessionKind.Subagent;

  public override string? GetText(StartingContextInput input) =>
      "Complete only the assigned task, stay within its scope, and return a concise result with findings, verification, and blockers.";
}
