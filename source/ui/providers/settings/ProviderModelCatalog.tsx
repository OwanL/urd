import { useState } from "preact/hooks";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { ProviderModelFamily } from "./ProviderModelFamily.tsx";
import type { ModelSettings } from "../ModelSettings.ts";

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
  const records = state.modelSettings[connection.id];
  const drafts = state.modelDrafts[connection.id] ?? [];
  const savedByModel = new Map(records?.map((item) => [item.modelId, item]));
  const draftsByModel = new Map(drafts.map((item) => [item.modelId, item]));
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
      {state.errors[`modelSettings:${connection.id}`] && (
        <p class="provider-settings-error">{state.errors[`modelSettings:${connection.id}`]}</p>
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
              {catalog.models.map((model) => {
                const saved = savedByModel.get(model.id);
                const draft = draftsByModel.get(model.id);
                const settings: ModelSettings = draft ??
                  saved ?? {
                    connectionId: connection.id,
                    modelId: model.id,
                    family: "other",
                    supportsImages: false,
                  };
                const changed =
                  draft &&
                  (!saved ||
                    draft.family !== saved.family ||
                    draft.supportsImages !== saved.supportsImages);
                const unavailable = disabled || !records;
                return (
                  <li key={model.id}>
                    <span>{model.name}</span>
                    <code>{model.id}</code>
                    <div class="provider-settings-model-controls">
                      <ProviderModelFamily
                        family={settings.family}
                        modelName={model.name}
                        disabled={unavailable}
                        onChange={(family) => state.actions.editModel(settings, { family })}
                      />
                      <label>
                        <input
                          type="checkbox"
                          aria-label={`${model.name} images`}
                          checked={settings.supportsImages}
                          disabled={unavailable}
                          onChange={(event) =>
                            state.actions.editModel(settings, {
                              supportsImages: event.currentTarget.checked,
                            })
                          }
                        />
                        Images
                      </label>
                      <button
                        type="button"
                        aria-label={`Save ${model.name} settings`}
                        disabled={unavailable || !changed}
                        onClick={() => {
                          void state.actions.saveModelSettings(settings);
                        }}
                      >
                        Save
                      </button>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </details>
      )}
    </div>
  );
}
