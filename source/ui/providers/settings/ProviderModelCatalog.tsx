import { useState } from "preact/hooks";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";

export function ProviderModelCatalog({
  state,
  connection,
  disabled,
}: {
  state: ProviderSettingsState;
  connection: ProviderConnection;
  disabled: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const catalog = state.catalogs[connection.id];
  return (
    <div class="provider-settings-group">
      <div class="provider-settings-group-heading">
        <h4>Models</h4>
        <button
          type="button"
          disabled={disabled}
          onClick={() => {
            void state.actions.refreshModels(connection);
          }}
        >
          Refresh models
        </button>
      </div>
      {state.errors[`models:${connection.id}`] && (
        <p class="provider-settings-error">{state.errors[`models:${connection.id}`]}</p>
      )}
      {!catalog && <p class="provider-settings-secondary">Catalog status unknown.</p>}
      {catalog && !catalog.refreshedAt && (
        <p class="provider-settings-secondary">No models discovered yet.</p>
      )}
      {catalog?.refreshedAt && (
        <details onToggle={(event) => setExpanded(event.currentTarget.open)}>
          <summary>
            {catalog.models.length} saved {catalog.models.length === 1 ? "model" : "models"}
          </summary>
          {expanded && (
            <ul class="provider-settings-models">
              {catalog.models.map((model) => (
                <li key={model.id}>
                  <span>{model.name}</span>
                  <code>{model.id}</code>
                </li>
              ))}
            </ul>
          )}
        </details>
      )}
    </div>
  );
}
