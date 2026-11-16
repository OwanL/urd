namespace Urd.Core.Context;

// Skills and instruction documents available from project and packaged resources.
internal sealed record DiscoveredInstructionResources(
  IReadOnlyList<DiscoveredSkill> Skills,
  IReadOnlyList<DiscoveredInstructionDocument> Instructions);
