namespace Urd.Core.Context;

internal static class SkillPruningGuidance {
  internal const string Text = """
      Choose the skills that would help the agent carry out the task.

      - Use the name, description and path to judge the fit. The directory tells you where a skill comes from, not where it can be used. Keep a portable skill; skip a harness-specific workflow the task doesn't need.
      - DescriptionRef means two files share a description, even if it isn't shown. Their instructions can still differ, so judge each file on its own.
      """;
}
