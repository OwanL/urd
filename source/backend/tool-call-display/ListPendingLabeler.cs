namespace Urd.Core.ToolCallDisplay;

internal static class ListPendingLabeler {
  internal static ToolCallLabel Create() =>
    ToolCallLabels.CreateLabel(ToolCallIcon.ListPending, "pending calls", null, null);
}
