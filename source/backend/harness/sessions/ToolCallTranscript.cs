namespace Urd.Core.Sessions;

// Token counts estimate each full model-facing field, even when the text is a truncated preview.
public sealed record ToolCallTranscript(string Input, string? Output, bool Truncated, bool ProjectionFinal,
    long? InputTokens, long? OutputTokens);
