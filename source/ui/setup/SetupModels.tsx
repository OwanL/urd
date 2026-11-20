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
}: {
  client: ProviderClient;
  state: PreferencesState;
  catalogLoading: boolean;
  editing: boolean;
  onEdit(): void;
}) {
  const choices = state.preferences?.modelChoices;
  const preview =
    !editing &&
    !!choices &&
    !(choices.small.length && choices.medium.length && choices.large.length);
  const suggestions = useBucketSuggestions(client, state.refresh, preview && !catalogLoading);
  const setup = suggestions.setup;

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
