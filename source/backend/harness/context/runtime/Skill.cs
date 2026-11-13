namespace Urd.Core.Context;

// A skill's SKILL.md file, advertised by name and description and read on demand.
internal sealed record Skill(string Name, string Description, string Path);
