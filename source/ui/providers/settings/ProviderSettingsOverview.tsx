import "./provider-settings-overview.css";
import type { RefObject } from "preact";
import { Icon } from "../../controls/Icon.tsx";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { connectionAttention, connectionTitle } from "./ProviderSettingsPresentation.ts";

export interface ProviderSearch {
  value: string;
  onChange(value: string): void;
  inputRef: RefObject<HTMLInputElement | null>;
}

export function ProviderSettingsOverview({
  state,
  search,
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
  search: ProviderSearch | undefined;
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
      {search && (
        <label class="provider-settings-search">
          <Icon name="search" />
          <input
            ref={search.inputRef}
            type="search"
            aria-label="Search providers"
            placeholder="Search providers…"
            value={search.value}
            onInput={(event) => search.onChange(event.currentTarget.value)}
          />
        </label>
      )}
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
