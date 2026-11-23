namespace Urd.Core.Preferences;

public sealed record UserPreferences(string? WorkingDirectory, int? CompactionTriggerTokenCount) {
  public bool LanHostingEnabled { get; init; }
  public AuxiliaryModelPreferences ModelChoices { get; init; } = new();
  // Null allows all available providers; an empty list allows none.
  public IReadOnlyList<Guid>? SubagentProviderConnectionIds { get; init; }
  public ProtectedResourcePreferences ProtectedResources { get; init; } = new();
  public SourceIgnoreRules SourceIgnores { get; init; } = new();
}
