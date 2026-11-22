import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { ProviderBucketSuggestions } from "../providers/settings/ProviderBucketSuggestions.tsx";
import { ModelPreferencesSettings } from "../settings/ModelPreferencesSettings.tsx";
import { useBucketSuggestions } from "./useBucketSuggestions.ts";

// Only onboarding puts a choice in front of the regular bucket editor.
export function SetupModels({
  client,
  state,
  catalogLoading,
  editing,
  onEdit,
  onSaved,
  onBusyChange,
}: {
  client: ProviderClient;
  state: PreferencesState;
  catalogLoading: boolean;
  editing: boolean;
  onEdit(): void;
  onSaved(): Promise<void>;
  onBusyChange(busy: boolean): void;
}) {
  const choices = state.preferences?.modelChoices;
  const suggestions = useBucketSuggestions(
    client,
    state.refresh,
    !editing && !!choices && !catalogLoading,
    onSaved,
    onBusyChange,
  );
  const setup = suggestions.setup;
  const preview =
    !editing &&
    !!choices &&
    (setup?.phase === "saving" ||
      !(choices.small.length && choices.medium.length && choices.large.length));

  if (preview && (catalogLoading || (setup && setup.phase !== "finished")))
    return (
      <div class="provider-settings">
        <ProviderBucketSuggestions
          setup={catalogLoading ? { phase: "loading" } : setup!}
          title="Suggested models"
          confirmLabel="Use suggested models"
          skipLabel="Choose my own"
          disabled={state.pending || !state.loaded}
          onConfirm={suggestions.confirm}
          onSkip={onEdit}
        />
      </div>
    );

  return (
    <>
      {preview && setup?.phase === "finished" && (
        <div class="provider-settings">
          <p
            role="status"
            class={setup.error ? "provider-settings-error" : "provider-settings-secondary"}
          >
            {setup.message}
          </p>
          {setup.error && (
            <button type="button" onClick={suggestions.retry}>
              Retry suggestions
            </button>
          )}
        </div>
      )}
      <ModelPreferencesSettings client={client} state={state} view="buckets" active />
    </>
  );
}
