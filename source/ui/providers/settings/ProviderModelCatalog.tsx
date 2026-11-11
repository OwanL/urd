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
                    contextWindow: null,
                  };
                const changed =
                  draft &&
                  (!saved ||
                    draft.family !== saved.family ||
                    draft.supportsImages !== saved.supportsImages ||
                    draft.contextWindow !== saved.contextWindow);
                const unavailable = disabled || !records;
                return (
                  <li key={model.id}>
                    <span>{model.name}</span>
                    <code>{model.id}</code>
                    <form
                      class="provider-settings-model-controls"
                      onSubmit={(event) => {
                        event.preventDefault();
                        void state.actions.saveModelSettings(settings);
                      }}
                    >
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
                      <label>
                        Context window
                        <input
                          type="number"
                          aria-label={`${model.name} context window override (blank uses catalog default)`}
                          min={1}
                          max={2_147_483_647}
                          step={1}
                          placeholder={
                            model.contextWindow == null ? undefined : String(model.contextWindow)
                          }
                          value={settings.contextWindow ?? ""}
                          disabled={unavailable}
                          onChange={(event) => {
                            const input = event.currentTarget;
                            if (!input.validity.valid) return;
                            const contextWindow = input.value === "" ? null : input.valueAsNumber;
                            if (
                              contextWindow === null ||
                              (Number.isSafeInteger(contextWindow) && contextWindow > 0)
                            )
                              state.actions.editModel(settings, { contextWindow });
                          }}
                        />
                      </label>
                      <button
                        type="submit"
                        aria-label={`Save ${model.name} settings`}
                        disabled={unavailable || !changed}
                      >
                        Save
                      </button>
                    </form>
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
