namespace Urd.Core.Context;

// A skill's SKILL.md file, advertised by name and description and read on demand.
internal sealed record DiscoveredSkill(string Name, string Description, string Path) {
  internal string Identity => Path;
}
