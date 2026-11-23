using System.Text.Json.Serialization;

namespace Urd.Core.Preferences;

public sealed record SourcePreview(
    [property: JsonPropertyName("paths")] string[] Paths,
    [property: JsonPropertyName("workingDirectory")] string WorkingDirectory,
    [property: JsonPropertyName("eligibleCount")] int EligibleCount,
    [property: JsonPropertyName("estimatedTokens")] int EstimatedTokens,
    [property: JsonPropertyName("partial")] bool Partial);
