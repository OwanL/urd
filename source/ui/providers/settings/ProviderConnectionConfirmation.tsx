import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { confirmationText } from "./ProviderSettingsPresentation.ts";

export function ProviderConnectionConfirmation({ state }: { state: ProviderSettingsState }) {
  const confirmation = state.confirmation;
  if (!confirmation) return null;
  return (
    <div class="provider-settings-confirmation" role="group" aria-label="Confirm connection change">
      <p>{confirmationText(confirmation.kind, confirmation.connection)}</p>
      <div class="provider-settings-actions">
        <button
          type="button"
          class="provider-settings-danger"
          onClick={() => {
            void state.actions.confirm();
          }}
        >
          Confirm{" "}
          {confirmation.kind === "binding"
            ? "binding change"
            : confirmation.kind === "remove"
              ? "local removal"
              : "disconnect"}
        </button>
        <button type="button" onClick={() => state.actions.dismissConfirmation()}>
          Keep connection
        </button>
      </div>
    </div>
  );
}
