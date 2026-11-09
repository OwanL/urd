import { useId } from "preact/hooks";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ConnectionDraft } from "./ProviderSettingsController.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";

export const providerNames = {
  chatGpt: "ChatGPT",
  openAI: "OpenAI",
  custom: "Custom",
} as const;

export function ConnectionForm({
  state,
  connection,
  disabled,
  onCreate,
}: {
  state: ProviderSettingsState;
  connection?: ProviderConnection;
  disabled: boolean;
  onCreate?: () => void;
}) {
  const id = useId();
  const { actions } = state;
  const draft = connection ? state.drafts[connection.id] : state.add;
  if (!draft) return null;
  const edit = (update: Partial<ConnectionDraft>) =>
    connection ? actions.edit(connection.id, update) : actions.editAdd(update);

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (connection) void actions.save(connection);
        else onCreate?.();
      }}
    >
      <fieldset disabled={disabled}>
        {(connection?.route ?? state.add.route) === "custom" && (
          <CustomFields id={id} draft={draft} edit={edit} />
        )}
        {connection && connection.route !== "custom" && (
          <p class="provider-settings-secondary">
            {providerNames[connection.route]} ·{" "}
            {connection.api === "openAIResponses" ? "Responses" : "Chat Completions"}
          </p>
        )}
      </fieldset>
      <div class="provider-settings-actions">
        <button type="submit" class="provider-settings-primary" disabled={disabled}>
          {connection ? "Save changes" : "Add provider"}
        </button>
      </div>
    </form>
  );
}

function CustomFields({
  id,
  draft,
  edit,
}: {
  id: string;
  draft: ConnectionDraft;
  edit: (update: Partial<ConnectionDraft>) => void;
}) {
  return (
    <>
      <label class="provider-settings-field">
        Endpoint
        <input
          type="url"
          required
          value={draft.endpoint}
          autoComplete="off"
          spellcheck={false}
          placeholder="https://example.com/v1/"
          onInput={(event) => edit({ endpoint: event.currentTarget.value })}
        />
      </label>
      <details class="provider-settings-details">
        <summary>Advanced</summary>
        <div class="provider-settings-form-options">
          <fieldset class="provider-settings-choices">
            <legend>Protocol</legend>
            {(
              [
                ["openAIResponses", "Responses"],
                ["openAIChatCompletions", "Chat Completions"],
              ] as const
            ).map(([value, label]) => (
              <label key={value}>
                <input
                  type="radio"
                  name={`${id}-api`}
                  checked={draft.api === value}
                  onChange={() => edit({ api: value })}
                />
                {label}
              </label>
            ))}
          </fieldset>
          <fieldset class="provider-settings-choices">
            <legend>Authentication</legend>
            {(
              [
                ["apiKey", "API key"],
                ["none", "None"],
              ] as const
            ).map(([value, label]) => (
              <label key={value}>
                <input
                  type="radio"
                  name={`${id}-auth`}
                  checked={draft.authentication === value}
                  onChange={() => edit({ authentication: value })}
                />
                {label}
              </label>
            ))}
          </fieldset>
        </div>
      </details>
      {draft.endpoint.trim().toLowerCase().startsWith("http:") && (
        <p class="provider-settings-warning">HTTP does not protect keys or requests in transit.</p>
      )}
    </>
  );
}
