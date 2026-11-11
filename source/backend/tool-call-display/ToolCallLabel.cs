namespace Urd.Core.ToolCallDisplay;

public enum ToolCallIcon { Terminal, Read, Write, Edit, ListPending, CancelPending, Generic }

public sealed record ToolCallLabel(ToolCallIcon Icon, string Title, string? Detail, string? Path);
