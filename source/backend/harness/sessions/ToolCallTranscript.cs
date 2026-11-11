namespace Urd.Core.Sessions;

public sealed record ToolCallTranscript(string Input, string? Output, bool Truncated);
