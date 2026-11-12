using Urd.Core.Sessions;

namespace Urd.Core.ToolCallDisplay;

public static class ToolCallLabelSet {
  public static IReadOnlyDictionary<string, ToolCallLabel> Create(
      IEnumerable<TranscriptEntryDescriptor> descriptors, string? workingDirectory) {
    ArgumentNullException.ThrowIfNull(descriptors);
    var labels = new Dictionary<string, ToolCallLabel>(StringComparer.Ordinal);
    foreach (TranscriptEntryDescriptor descriptor in descriptors) {
      if (descriptor.Tool is not { } tool)
        continue;

      string key = $"{tool.RequestId:D}/{tool.CallId}";
      if (!labels.ContainsKey(key))
        labels.Add(key, ToolCallLabels.Create(tool.Name, tool.ArgumentsExcerpt, workingDirectory));
    }

    return labels;
  }
}
