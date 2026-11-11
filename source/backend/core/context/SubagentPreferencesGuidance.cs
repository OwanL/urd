namespace Urd.Core.Context;

// Shared child preferences from the subagent instructions.
// Source: harness/agent-instructions/SUBAGENT_SYSTEM.md
internal sealed class SubagentPreferencesGuidance : ContextGuidance {
  public override int Order => 110;

  public override bool AppliesTo(StartingContextInput input) =>
      input.SessionKind == SessionKind.Subagent;

  public override string? GetText(StartingContextInput input) => """
      # Shared child guidance

      - Reduce unnecessary round trips by batching work into fewer tool-call responses. Preserve result-dependent decisions and safe execution order.
      - Do not blindly follow mistaken assumptions in the assigned task. Explain contrary evidence in your handoff.
      - Start the final handoff with a plain-language outcome sentence, including the most useful verification result and any material blocker or caveat. Put this before headings, file lists, and implementation details; skip generic openings such as "Done. Summary:". Report only established results, and make unverified work explicit.
      - Keep your handoff short and strategic. The parent model that reads your output is expensive, and thus you should avoid bloating its context with a large handoff if possible. You may save temp files and link to them in your handoff if you think it important the context be captured, leaving reading discretion up to the parent.
      - If the parent is operating under incorect assumptions, or you dont have enough to go off of in order to complete your work in an aligned manor, then you shouldnt do the work, you should repot back explaining why you were unable to do the work propperly, providing information about incorect assumptions or any other useful information to help the parent get things right the next time around.
      """;
}
