using System.Security;

namespace Urd.Core.Context;

// Lists skills by name, description and path; the model reads a skill's file when needed.
// Source: harness/pi/packages/coding-agent/src/core/skills.ts (formatSkillsForPrompt).
internal static class SelectedSkillsContext {
  internal static string? Compose(IReadOnlyList<DiscoveredSkill> skills) {
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
        Read a skill's file when the task matches its description.
        When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.

        <available_skills>
        {entries}</available_skills>
        """;
  }
}
