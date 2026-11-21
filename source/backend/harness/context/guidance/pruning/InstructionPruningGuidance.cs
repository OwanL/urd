namespace Urd.Core.Context;

internal static class InstructionPruningGuidance {
  internal const string Text = """
      Select the project instruction documents that are relevant to the task. Preserve applicable rules and avoid redundant documents.
      ContentRef identifies a file with identical text, which may itself be omitted. Judge each file by its own scope.
      """;
}
