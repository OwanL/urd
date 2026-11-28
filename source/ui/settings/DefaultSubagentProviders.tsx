import { useState } from "preact/hooks";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import { providerReady } from "../providers/settings/ProviderConnectionStatus.ts";
import type { ProviderSettingsState } from "../providers/settings/useProviderSettings.ts";
import { SubagentProviderFields } from "../providers/subagents/SubagentProviderFields.tsx";
import type { SubagentProviders } from "../providers/subagents/SubagentProviderSelection.ts";

const preference = "subagentProviderConnectionIds";

export function DefaultSubagentProviders({
  providers,
  state,
}: {
  providers: ProviderSettingsState;
  state: PreferencesState;
}) {
  const [draft, setDraft] = useState<SubagentProviders | null>(null);
  const saved = state.preferences;
  const readError = providers.errors.connections;
  const pending = state.pendingFor === preference;
  const error = state.loaded ? state.errorFor(preference) : null;

  return (
    <section
      class="default-subagent-providers general-settings"
      aria-label="Subagent provider defaults"
    >
      {saved && providers.loaded ? (
        <SubagentProviderFields
          label="Providers for new chats"
          connections={providers.connections.filter((connection) =>
            providerReady(connection, providers.credentials[connection.id]),
          )}
          selection={draft ? draft.connectionIds : saved.subagentProviderConnectionIds}
          disabled={state.pending || draft !== null || Boolean(readError)}
          onChange={(connectionIds) => {
            setDraft({ connectionIds });
            void state.saveSubagentProviders(connectionIds).finally(() => setDraft(null));
          }}
        />
      ) : (
        <h4>Providers for new chats</h4>
      )}
      <div class="general-settings-feedback" aria-live="polite">
        {pending && <p role="status">Saving…</p>}
        {error && <p role="alert">{error}</p>}
        {readError && (
          <>
            <p role="alert">Provider connections could not be loaded.</p>
            <button
              type="button"
              disabled={providers.reading}
              onClick={() => void providers.actions.read()}
            >
              Retry provider connections
            </button>
          </>
        )}
        {!providers.loaded && !readError && <p role="status">Loading provider connections…</p>}
      </div>
    </section>
  );
}
