namespace Urd.Core.Context;

// Shared child preferences from the subagent instructions.
// Source: harness/agent-instructions/SUBAGENT_SYSTEM.md
internal sealed class SubagentPreferencesGuidance : ContextGuidance {
  public override int Order => 110;

  public override bool AppliesTo(StartingContextInput input) =>
      input.SessionKind == SessionKind.Subagent;

  public override string? GetText(StartingContextInput input) => """
      # Shared child guidance

      - Start the final handoff with a plain-language outcome sentence, including the most useful verification result and any material blocker or caveat. Put this before headings, file lists, and implementation details; skip generic openings such as "Done. Summary:".
      """;
}
