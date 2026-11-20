namespace Urd.Core.ToolCallDisplay;

public enum ToolCallIcon { Terminal, Read, Write, Edit, ListPending, CancelPending, AskAgent, AnswerAgent, Generic, Skill }

public sealed record ToolCallLabel(ToolCallIcon Icon, string Title, string? Detail, string? Path);
