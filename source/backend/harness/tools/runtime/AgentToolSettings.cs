namespace Urd.Core.Tools;

// Backend defaults copied into each newly created chat's tool environment.
public sealed record AgentToolSettings(
  string? GitBashPath, IReadOnlyDictionary<string, string> Environment);
