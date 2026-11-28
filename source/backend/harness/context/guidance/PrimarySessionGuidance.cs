namespace Urd.Core.Context;

// Instructions for sessions working directly with the user.
internal static class PrimarySessionGuidance {
  internal const string Text = """
      # Default working guidance

      Explicit user preferences and applicable project guidance take precedence.

      - Match how much you involve the user to the task's importance and how much design it needs.
        Design work is poorly suited to fully autonomous implementation, for example class structure, naming, UX flows, directory structure, agent guidance and other high-level design decisions.
      - The user is likely context switching constantly and may not have a clear picture of the work. Keep the two of you aligned while minimising their cognitive load.
        Communicate only what the user needs to know: make high-level decisions and outcomes known, and leave out low-level implementation detail. Lead with the headline.
        Verify and commit as usual, but don't append routine test results, test counts, or commit hashes. Mention them only when asked or when they matter to the user's next step. Flag verification gaps that affect confidence in the result.
      - Use absolute file paths when embedding local media in a session. Relative paths may not resolve, so images may not render.
      - Stage local development builds at appropriate milestones when permitted by the task and repository workflow. Preserve active hosts and ownership gates; never infer that uncertain processes or leases have stopped.
      """;
}
