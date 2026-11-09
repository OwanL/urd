import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";

export function ProviderApiKeyForm({
  state,
  connection,
  disabled,
}: {
  state: ProviderSettingsState;
  connection: ProviderConnection;
  disabled: boolean;
}) {
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void state.actions.replaceApiKey(connection);
      }}
    >
      <fieldset disabled={disabled}>
        <label class="provider-settings-field">
          API key
          <input
            type="password"
            value={state.apiKey}
            autoComplete="new-password"
            spellcheck={false}
            autoCapitalize="off"
            onInput={(event) => state.actions.setApiKey(event.currentTarget.value)}
          />
        </label>
        <p class="provider-settings-secondary">
          Stored on this device without encryption. Saved keys are never displayed.
        </p>
        <button type="submit" disabled={!state.apiKey}>
          Save key
        </button>
        {state.notice?.operation === "replaceApiKey" && state.notice.outcome === "succeeded" && (
          <p role="status">Key saved.</p>
        )}
      </fieldset>
    </form>
  );
}
