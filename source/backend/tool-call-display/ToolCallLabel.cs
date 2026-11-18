namespace Urd.Core.ToolCallDisplay;

public enum ToolCallIcon { Terminal, Read, Write, Edit, ListPending, CancelPending, AskAgent, AnswerAgent, Generic }

public sealed record ToolCallLabel(ToolCallIcon Icon, string Title, string? Detail, string? Path);
