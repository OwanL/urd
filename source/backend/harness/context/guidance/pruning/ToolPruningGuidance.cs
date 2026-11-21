namespace Urd.Core.Context;

internal static class ToolPruningGuidance {
  internal const string Text = """
      Choose the tools the agent needs to do the work.

      - Think through the likely steps, including how the agent will check its result. Keep tools that enable those steps even if the user didn't name them.
      - Use the description and parameters to judge what a tool can do. You don't need to run it to decide whether it belongs.
      """;
}
