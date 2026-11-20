import { useEffect, useState } from "preact/hooks";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { ProviderBucketSuggestions } from "../providers/settings/ProviderBucketSuggestions.tsx";
import { ModelPreferencesSettings } from "./ModelPreferencesSettings.tsx";
import { useBucketSuggestions } from "./useBucketSuggestions.ts";

const buckets = ["small", "medium", "large"] as const;

/**
 * Model bucket assignment shared by Settings and first-run setup. While a bucket is empty,
 * suggested models are offered first; choosing your own reveals the detailed assignments.
 */
export function ModelBucketsPanel({
  client,
  state,
  active,
}: {
  client: ProviderClient;
  state: PreferencesState;
  active: boolean;
}) {
  const incomplete =
    state.preferences !== null &&
    buckets.some((bucket) => !state.preferences!.modelChoices[bucket].length);
  // Decide once per visit so emptying a bucket mid-edit does not replace the list.
  const [offer, setOffer] = useState<boolean | null>(null);
  useEffect(() => {
    if (!active) setOffer(null);
    else if (offer === null && state.preferences) setOffer(incomplete);
  }, [active, offer, state.preferences, incomplete]);
  const suggestions = useBucketSuggestions(client, state.refresh, active && !!offer && incomplete);
  const setup = suggestions.setup;

  if (setup && setup.phase !== "finished")
    return (
      <div class="provider-settings model-bucket-suggestions">
        <ProviderBucketSuggestions
          setup={setup}
          title="Suggested models"
          confirmLabel="Use suggested models"
          skipLabel="Choose my own"
          disabled={state.pending || !state.loaded}
          onConfirm={suggestions.confirm}
          onSkip={() => setOffer(false)}
        />
      </div>
    );

  return (
    <>
      {setup && (
        <div class="provider-settings model-bucket-suggestions">
          <p
            role="status"
            class={setup.error ? "provider-settings-error" : "provider-settings-secondary"}
          >
            {setup.message}
          </p>
          {setup.error && (
            <div class="provider-settings-actions">
              <button type="button" onClick={suggestions.retry}>
                Retry suggestions
              </button>
            </div>
          )}
        </div>
      )}
      <ModelPreferencesSettings client={client} state={state} view="buckets" active={active} />
    </>
  );
}
