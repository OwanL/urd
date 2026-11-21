namespace Urd.Core.Context;

// Instructions for carrying out and reporting a delegated task.
internal static class SubagentSessionGuidance {
  internal const string Text = """
      You are a child session completing a delegated task. Stay within its scope.
      Give a concise final report with the outcome, changed paths, verification performed, and any blockers.
      Start with a plain-language outcome sentence, including the most useful verification result and any material blocker or caveat. Put it before headings, file lists, and implementation details; skip generic openings such as "Done. Summary:".
      """;
}
