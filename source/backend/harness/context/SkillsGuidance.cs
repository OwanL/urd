using System.Security;

namespace Urd.Core.Context;

// Lists skills by name, description and path; the model reads a skill's file when needed.
// Source: harness/pi/packages/coding-agent/src/core/skills.ts (formatSkillsForPrompt).
internal sealed class SkillsGuidance : ContextGuidance {
  public override int Order => 220;

  public override bool AppliesTo(StartingContextInput input) =>
      input.WorkingDirectory is not null;

  public override string? GetText(StartingContextInput input) =>
      Compose(SkillCatalog.Find(input.WorkingDirectory!, SkillCatalog.PackagedDirectory));

  internal static string? Compose(IReadOnlyList<Skill> skills) {
    if (skills.Count == 0)
      return null;

    string entries = string.Concat(skills.Select(skill => $"""
          <skill>
            <name>{SecurityElement.Escape(skill.Name)}</name>
            <description>{SecurityElement.Escape(skill.Description)}</description>
            <location>{SecurityElement.Escape(skill.Path)}</location>
          </skill>

        """));
    return $"""
        The following skills provide specialized instructions for specific tasks.
        Use the read tool to load a skill's file when the task matches its description.
        When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.

        <available_skills>
        {entries}</available_skills>
        """;
  }
}
