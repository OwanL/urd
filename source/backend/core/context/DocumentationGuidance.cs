namespace Urd.Core.Context;

// Shared documentation workflow guidance.
// Source: harness/agent-instructions/prompt-assembly/pie-harness-prompt.ts
internal sealed class DocumentationGuidance : ContextGuidance {
  public override int Order => 60;

  public override string? GetText(StartingContextInput input) =>
      "- Read the sections that own the behavior you are changing and follow references needed to resolve the task. Read the full normative contract when changing its invariants. Stop when the relevant requirements and constraints are understood.";
}
