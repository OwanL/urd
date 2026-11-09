import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { noticeText, operationLabels } from "./ProviderSettingsPresentation.ts";

export function ProviderSettingsFeedback({ state }: { state: ProviderSettingsState }) {
  const notice = state.notice ? noticeText(state.notice) : null;
  return (
    <div class="provider-settings-feedback" aria-live="polite" aria-atomic="true">
      {state.pending && <p>{operationLabels[state.pending]}…</p>}
      {notice && (
        <p
          class={
            state.notice?.outcome === "uncertain" || state.notice?.outcome === "rejected"
              ? "provider-settings-error"
              : ""
          }
        >
          {notice}
        </p>
      )}
      {state.uncertain && state.connectionsReloadedAfterUncertainty && (
        <div class="provider-settings-warning">
          <p>Review the saved connections and status. Another add may create a duplicate.</p>
          <button type="button" onClick={() => state.actions.allowNewCommands()}>
            I reviewed the state — allow new commands
          </button>
        </div>
      )}
      {state.errors.connections && (
        <p class="provider-settings-error">{state.errors.connections}</p>
      )}
    </div>
  );
}
