namespace Urd.Core.Context;

internal static class InstructionPruningGuidance {
  internal const string Text = """
      Choose the project instructions that govern the work.

      - Consider where the agent will work and what it's likely to do. A workspace rule can apply even when the user doesn't mention it; a rule for a nested directory may not.
      - Keep portable guidance. Drop instructions specific to another harness unless the task calls for its workflow.
      - ContentRef means two files have identical text, even if that text isn't shown. Check their scopes before treating one as redundant.
      """;
}
