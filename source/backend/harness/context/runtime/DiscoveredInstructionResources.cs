namespace Urd.Core.Context;

// Skills and instruction documents available from project and packaged resources, with the
// freshness of the project paths served by the instruction-resource index.
internal sealed record DiscoveredInstructionResources(
  IReadOnlyList<DiscoveredSkill> Skills,
  IReadOnlyList<DiscoveredInstructionDocument> Instructions,
  InstructionResourceIndexState Index = default);
