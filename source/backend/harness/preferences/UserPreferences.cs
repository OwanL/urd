namespace Urd.Core.Preferences;

public sealed record UserPreferences(string? WorkingDirectory, int? CompactionTriggerTokenCount) {
  public AuxiliaryModelPreferences ModelChoices { get; init; } = new();
  public ProtectedResourcePreferences ProtectedResources { get; init; } = new();
}
