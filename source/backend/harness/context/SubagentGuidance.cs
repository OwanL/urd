namespace Urd.Core.Context;

// Instructions for sessions carrying out a delegated task.
// Source: harness/agent-instructions/defaults/SUBAGENT_SYSTEM.md
internal sealed class SubagentGuidance : StartingContextContribution {
  public override int Order => 100;

  public override bool AppliesTo(StartingContextInput input) =>
      input.SessionKind == SessionKind.Subagent;

  public override string? GetText(StartingContextInput input) => """
      You are a child session completing a delegated task. Stay within its scope and return a concise final report with the outcome, changed paths, verification performed, and any blockers.
      """;
}
