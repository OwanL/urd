import { useState } from "preact/hooks";
import type { ProviderConnection } from "../ProviderConnection.ts";
import { ConnectionForm, providerNames } from "./ConnectionForm.tsx";
import {
  credentialSummary,
  matchesConnection,
  signInPending,
} from "./ProviderSettingsController.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import "./provider-settings.css";

interface ProviderSettingsProps {
  state: ProviderSettingsState;
  search: string;
}

export function ProviderSettings({ state, search }: ProviderSettingsProps) {
  if (!state.active) return null;
  const { actions } = state;
  const selected = state.connections.find((connection) => connection.id === state.selectedId);
  const visible = state.connections.filter((connection) => matchesConnection(connection, search));
  const locked = state.pending !== null || state.uncertain;
  const draft = selected ? state.drafts[selected.id] : state.add;
  const credential = selected ? state.credentials[selected.id] : undefined;
  const signIn = selected ? state.signIns[selected.id] : undefined;
  const confirming = state.confirmation !== null;
  const canEdit = !locked && !confirming;

  return (
    <section class="provider-settings" aria-label="Providers" aria-busy={state.reading}>
      <div class="provider-settings-toolbar">
        <button type="button" onClick={() => actions.select(null)} aria-pressed={!selected}>
          Add connection
        </button>
        <button
          type="button"
          disabled={state.reading || state.pending !== null}
          onClick={() => {
            void actions.read();
          }}
        >
          {state.reading ? "Reading…" : "Read saved state"}
        </button>
      </div>

      <div class="provider-settings-feedback" aria-live="polite" aria-atomic="true">
        {state.pending && <p>{state.pending}… Closing settings will not cancel this command.</p>}
        {state.notice && (
          <p class={state.uncertain ? "provider-settings-error" : ""}>{state.notice}</p>
        )}
        {state.uncertain && state.reviewed && (
          <div class="provider-settings-warning">
            <p>Review the saved connections and status. Another add may create a duplicate.</p>
            <button type="button" onClick={() => actions.allowNewCommands()}>
              I reviewed the state — allow new commands
            </button>
          </div>
        )}
        {state.errors.connections && (
          <p class="provider-settings-error">{state.errors.connections}</p>
        )}
      </div>

      <div class="provider-settings-connections" aria-label="Saved connections">
        {!state.loaded && !state.reading && <p>Saved connections have not been read.</p>}
        {state.loaded && !state.connections.length && <p>No saved connections.</p>}
        {!!state.connections.length && !visible.length && <p>No matching connections.</p>}
        {visible.map((connection) => (
          <button
            type="button"
            key={connection.id}
            class="provider-settings-connection"
            aria-pressed={selected?.id === connection.id}
            onClick={() => actions.select(connection.id)}
          >
            <span class="provider-settings-connection-heading">
              <strong>{connection.name}</strong>
              <span>{providerNames[connection.route]}</span>
            </span>
            <span class="provider-settings-endpoint">{connection.endpoint}</span>
            <span class="provider-settings-secondary">
              {credentialSummary(connection, state.credentials[connection.id])}
              {state.errors[`credentials:${connection.id}`] && " · read failed; may be stale"}
            </span>
          </button>
        ))}
      </div>

      {draft && (
        <section
          class="provider-settings-card"
          aria-label={selected ? "Connection settings" : "New connection"}
        >
          <h3>{selected ? selected.name : "Add connection"}</h3>
          <ConnectionForm state={state} connection={selected} disabled={!canEdit} />
          {state.confirmation?.kind === "binding" && <ConfirmChange state={state} />}

          {selected && (
            <>
              <div class="provider-settings-group">
                <h4>Authentication</h4>
                <p>{credentialSummary(selected, credential)}</p>
                {state.errors[`credentials:${selected.id}`] && (
                  <p class="provider-settings-error">
                    {state.errors[`credentials:${selected.id}`]}
                  </p>
                )}
                {credential && (credential.accountLabel || credential.expiresAt) && (
                  <p class="provider-settings-secondary">
                    {credential.accountLabel && <span>{credential.accountLabel}</span>}
                    {credential.expiresAt && <span>Expires: {credential.expiresAt}</span>}
                  </p>
                )}
                {selected.authentication === "apiKey" && (
                  <form
                    onSubmit={(event) => {
                      event.preventDefault();
                      void actions.replaceApiKey(selected);
                    }}
                  >
                    <fieldset disabled={!canEdit}>
                      <label class="provider-settings-field">
                        Replacement API key
                        <input
                          type="password"
                          value={state.apiKey}
                          autoComplete="new-password"
                          spellcheck={false}
                          autoCapitalize="off"
                          onInput={(event) => actions.setApiKey(event.currentTarget.value)}
                        />
                      </label>
                      <p class="provider-settings-secondary">
                        Saved locally without encryption. Existing keys are never displayed.
                      </p>
                      <button type="submit" disabled={!state.apiKey}>
                        Replace API key
                      </button>
                    </fieldset>
                  </form>
                )}
                {selected.route === "chatGpt" && (
                  <div class="provider-settings-sign-in">
                    {signIn && (
                      <p>
                        Sign-in: {signIn.status}
                        {signIn.error ? ` · ${signIn.error}` : ""}
                      </p>
                    )}
                    {state.errors[`signIn:${selected.id}`] && (
                      <p class="provider-settings-error">{state.errors[`signIn:${selected.id}`]}</p>
                    )}
                    {signIn?.status === "pending" && authorizationLink(signIn.authorizationUrl) && (
                      <a
                        class="provider-settings-action"
                        href={authorizationLink(signIn.authorizationUrl)!}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        Open external sign-in ↗
                      </a>
                    )}
                    {signIn && signInPending(signIn) && (
                      <p class="provider-settings-secondary">Expires: {signIn.expiresAt}</p>
                    )}
                    <div class="provider-settings-actions">
                      <button
                        type="button"
                        disabled={!canEdit || (!!signIn && signInPending(signIn))}
                        onClick={() => {
                          void actions.beginSignIn(selected);
                        }}
                      >
                        Begin sign-in
                      </button>
                      {signIn && (
                        <button
                          type="button"
                          disabled={state.reading || state.pending !== null}
                          onClick={() => {
                            void actions.read();
                          }}
                        >
                          Get status
                        </button>
                      )}
                      {signIn && signInPending(signIn) && (
                        <button
                          type="button"
                          disabled={!canEdit}
                          onClick={() => {
                            void actions.cancelSignIn(signIn);
                          }}
                        >
                          Cancel sign-in
                        </button>
                      )}
                    </div>
                  </div>
                )}
              </div>

              <SavedModels
                key={selected.id}
                state={state}
                connection={selected}
                disabled={!canEdit}
              />

              <div class="provider-settings-group">
                <div class="provider-settings-actions">
                  <button
                    type="button"
                    disabled={!canEdit}
                    onClick={() => actions.requestConfirmation("disconnect", selected)}
                  >
                    Disconnect
                  </button>
                  <button
                    type="button"
                    class="provider-settings-danger"
                    disabled={!canEdit}
                    onClick={() => actions.requestConfirmation("remove", selected)}
                  >
                    Remove locally
                  </button>
                </div>
              </div>
            </>
          )}
          {state.confirmation && state.confirmation.kind !== "binding" && (
            <ConfirmChange state={state} />
          )}
        </section>
      )}
    </section>
  );
}

function SavedModels({
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
        <h4>Saved models</h4>
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
        <p class="provider-settings-secondary">Never refreshed.</p>
      )}
      {catalog?.refreshedAt && (
        <details onToggle={(event) => setExpanded(event.currentTarget.open)}>
          <summary>
            {catalog.models.length} models · {catalog.refreshedAt}
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

function ConfirmChange({ state }: { state: ProviderSettingsState }) {
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

function authorizationLink(value: string | null): string | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value);
    return url.protocol === "https:" && !url.username && !url.password ? url.href : undefined;
  } catch {
    return undefined;
  }
}

function confirmationText(
  kind: "binding" | "disconnect" | "remove",
  connection: ProviderConnection,
): string {
  if (kind === "binding")
    return `Change the endpoint, API or authentication for “${connection.name}”? This clears credentials and saved models and may cancel active requests.`;
  if (kind === "disconnect")
    return `Disconnect “${connection.name}”? This may cancel active requests, clears local credentials and attempts remote revocation where applicable.`;
  return `Remove “${connection.name}” from this device only? This deletes local configuration and credentials and may cancel active requests. It does not revoke remote access; disconnect first if needed.`;
}
