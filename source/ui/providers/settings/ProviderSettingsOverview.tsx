import { Icon } from "../../components/Icon.tsx";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { connectionAttention, connectionTitle } from "./ProviderSettingsPresentation.ts";

export function ProviderSettingsOverview({
  state,
  visible,
  complete,
  unknown,
  canEdit,
  canNavigate,
  returnId,
  overviewControl,
  onAddProvider,
  onOpenConnection,
}: {
  state: ProviderSettingsState;
  visible: readonly ProviderConnection[];
  complete: readonly ProviderConnection[];
  unknown: readonly ProviderConnection[];
  canEdit: boolean;
  canNavigate: boolean;
  returnId: string | null;
  overviewControl: { current: HTMLButtonElement | null };
  onAddProvider: () => void;
  onOpenConnection: (connection: ProviderConnection) => void;
}) {
  return (
    <>
      <header class="provider-settings-overview-heading">
        <h3>Providers</h3>
        <button
          ref={returnId && visible.some(({ id }) => id === returnId) ? null : overviewControl}
          type="button"
          class="provider-settings-add"
          disabled={!canEdit}
          onClick={onAddProvider}
        >
          <span aria-hidden="true">+</span> Add provider
        </button>
      </header>
      {(!state.loaded || !!unknown.length) && (
        <p class="provider-settings-secondary">
          {unknown.some((item) => state.errors[`credentials:${item.id}`])
            ? "Could not check some providers. Open Add provider to review."
            : state.reading || state.loaded
              ? "Checking provider setup…"
              : "Saved providers have not been read."}
        </p>
      )}
      {state.loaded && !state.errors.connections && !unknown.length && !complete.length && (
        <p class="provider-settings-secondary">No providers set up.</p>
      )}
      {state.loaded && !!complete.length && !visible.length && (
        <p class="provider-settings-secondary">No matching providers.</p>
      )}
      <ul class="provider-settings-connections" aria-label="Set-up providers">
        {visible.map((item) => {
          const attention = connectionAttention(state, item);
          return (
            <li key={item.id}>
              <button
                ref={returnId === item.id ? overviewControl : null}
                type="button"
                class="provider-settings-connection"
                disabled={!canNavigate}
                onClick={() => onOpenConnection(item)}
              >
                <strong>{connectionTitle(item)}</strong>
                {attention && <span class="provider-settings-secondary">{attention}</span>}
                <Icon name="chevron" />
              </button>
            </li>
          );
        })}
      </ul>
    </>
  );
}
