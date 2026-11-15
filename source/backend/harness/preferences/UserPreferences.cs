namespace Urd.Core.Preferences;

public sealed record UserPreferences(string? WorkingDirectory, int? CompactionTriggerTokenCount);
