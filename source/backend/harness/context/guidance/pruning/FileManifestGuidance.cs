namespace Urd.Core.Context;

internal static class FileManifestGuidance {
  internal const string Text = """
      The agent doing the work runs in Urd, regardless of the model or provider.
      Combine each directory with its file name for the full path; use that path and the other fields to judge where and for which harness its guidance applies.
      Drop guidance clearly meant only for another harness unless the task requires following that harness's workflow.
      A directory name alone does not make otherwise portable guidance irrelevant.
      An absent body or description without a reference was omitted, not evidence of irrelevance. SizeBytes may be null when unknown.
      Use the existing read tool with path, offset (1-indexed line), and limit (lines); make multiple read calls in the same response when useful, and follow continuation offsets.
      ContentRef and DescriptionRef refer only to the identical text field of another candidate ID, not to the candidate or its whole file; the referenced text may itself be omitted. Equal descriptions do not imply equal skill bodies.
      """;
}
