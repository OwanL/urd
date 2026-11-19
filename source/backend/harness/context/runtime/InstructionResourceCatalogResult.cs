namespace Urd.Core.Context;

// Paths served by the instruction-resource catalog plus how fresh those rows are.
internal sealed record InstructionResourceCatalogResult(
  IReadOnlyList<string> Paths,
  InstructionResourceIndexState State);
