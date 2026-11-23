using Urd.Core.Preferences;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  public async Task<SourcePreview> PreviewSourcesAsync(string category, SourceIgnoreRules rules, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(rules);
    bool skills = category switch {
      "instructions" => false,
      "skills" => true,
      _ => throw new ArgumentException("Source category must be 'instructions' or 'skills'.", nameof(category)),
    };
    SourceIgnoreRules normalized = rules.Normalize();
    UserPreferences defaults = await registry.RunOpenAsync(() => preferences.Read(fallbackWorkingDirectory), cancellation);
    return await store.InstructionDiscovery.PreviewAsync(defaults.WorkingDirectory!, skills, normalized,
        skills ? defaults.ProtectedResources.Skills : defaults.ProtectedResources.Instructions, cancellation);
  }
}
