import { useCallback, useId, useLayoutEffect, useState } from "preact/hooks";
import { Icon } from "../../components/Icon.tsx";
import type { ProviderConnection, ProviderRoute } from "../ProviderConnection.ts";
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

interface Presentation {
  adding: boolean;
  expandedId: string | null;
  creating: ReadonlySet<string> | null;
}

// Settings unmounts its content when closed. Keep presentation with its existing command owner.
const presentations = new WeakMap<ProviderSettingsState["actions"], Presentation>();

export function ProviderSettings({ state, search }: ProviderSettingsProps) {
  const { actions } = state;
  const id = useId();
  const [presentation, setPresentation] = useState<Presentation>(
    () => presentations.get(actions) ?? { adding: false, expandedId: null, creating: null },
  );
  const { adding, expandedId, creating } = presentation;
  const show = useCallback(
    (next: Presentation) => {
      presentations.set(actions, next);
      setPresentation(next);
    },
    [actions],
  );

  useLayoutEffect(() => {
    // Only a successful create selects a new ID. Rejections and uncertain reads stay in the form.
    if (
      creating &&
      state.selectedId &&
      !creating.has(state.selectedId) &&
      state.connections.some((connection) => connection.id === state.selectedId)
    )
      show({ adding: false, expandedId: state.selectedId, creating: null });
  }, [creating, state.selectedId, state.connections, show]);

  if (!state.active) return null;
  const visible = state.connections.filter(
    (connection) => connection.id === expandedId || matchesConnection(connection, search),
  );
  const canEdit = state.pending === null && !state.uncertain && state.confirmation === null;
  const choose = (route: ProviderRoute) => {
    actions.select(null);
    actions.editAdd({ route });
    show({ adding: true, expandedId: null, creating: null });
  };
  const collapse = () => {
    actions.select(null);
    show({ adding: false, expandedId: null, creating: null });
  };

  return (
    <section class="provider-settings" aria-label="Providers" aria-busy={state.reading}>
      <section class="provider-settings-add" aria-labelledby={`${id}-add`}>
        <h3 id={`${id}-add`}>Add a provider</h3>
        <div class="provider-settings-actions" aria-label="Provider type">
          {(Object.keys(providerNames) as ProviderRoute[]).map((route) => (
            <button
              type="button"
              key={route}
              aria-pressed={adding && state.add.route === route}
              disabled={state.pending !== null || state.uncertain}
              onClick={() => choose(route)}
            >
              {route === "openAI" ? "OpenAI" : providerNames[route]}
            </button>
          ))}
        </div>
        {adding && (
          <ConnectionForm
            state={state}
            disabled={!canEdit}
            onCancel={collapse}
            onCreate={() => {
              if (!canEdit) return;
              show({ ...presentation, creating: new Set(state.connections.map(({ id }) => id)) });
              actions.select(null);
              void actions.create();
            }}
          />
        )}
      </section>

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

      <section class="provider-settings-saved" aria-labelledby={`${id}-saved`}>
        <div class="provider-settings-group-heading">
          <h3 id={`${id}-saved`}>Your providers</h3>
          <button
            type="button"
            class="provider-settings-refresh"
            disabled={state.reading || state.pending !== null}
            onClick={() => {
              void actions.read();
            }}
          >
            <Icon name="refresh" />
            {state.reading ? "Refreshing…" : "Refresh status"}
          </button>
        </div>
        <div class="provider-settings-connections">
          {!state.loaded && !state.reading && <p>Saved providers have not been read.</p>}
          {state.loaded && !state.connections.length && <p>No saved providers.</p>}
          {!!state.connections.length && !visible.length && <p>No matching providers.</p>}
          {visible.map((connection) => {
            const expanded = expandedId === connection.id;
            const panelId = `${id}-connection-${connection.id}`;
            return (
              <div class="provider-settings-connection-item" key={connection.id}>
                <button
                  type="button"
                  class="provider-settings-connection"
                  aria-expanded={expanded}
                  aria-controls={expanded ? panelId : undefined}
                  onClick={() => {
                    if (expanded) collapse();
                    else {
                      actions.select(connection.id);
                      show({ adding: false, expandedId: connection.id, creating: null });
                    }
                  }}
                >
                  <span class="provider-settings-connection-heading">
                    <strong>{connection.name}</strong>
                    <span>{providerNames[connection.route]}</span>
                  </span>
                  <span class="provider-settings-secondary">
                    {credentialSummary(connection, state.credentials[connection.id])}
                    {state.errors[`credentials:${connection.id}`] && " · read failed; may be stale"}
                  </span>
                  <Icon name="chevron" />
                </button>
                {expanded && (
                  <div id={panelId} class="provider-settings-management">
                    <ConnectionManagement
                      state={state}
                      connection={connection}
                      disabled={!canEdit}
                    />
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </section>
    </section>
  );
}

function ConnectionManagement({
  state,
  connection,
  disabled,
}: {
  state: ProviderSettingsState;
  connection: ProviderConnection;
  disabled: boolean;
}) {
  const { actions } = state;
  const credential = state.credentials[connection.id];
  const signIn = state.signIns[connection.id];
  const confirmation =
    state.confirmation?.connection.id === connection.id ? state.confirmation : null;

  return (
    <>
      <details class="provider-settings-details">
        <summary>Edit connection details</summary>
        <ConnectionForm state={state} connection={connection} disabled={disabled} />
      </details>
      {confirmation?.kind === "binding" && <ConfirmChange state={state} />}

      <div class="provider-settings-group">
        <h4>Authentication</h4>
        <p>{credentialSummary(connection, credential)}</p>
        {state.errors[`credentials:${connection.id}`] && (
          <p class="provider-settings-error">{state.errors[`credentials:${connection.id}`]}</p>
        )}
        {credential && (credential.accountLabel || credential.expiresAt) && (
          <p class="provider-settings-secondary">
            {credential.accountLabel && <span>{credential.accountLabel}</span>}
            {credential.expiresAt && <span>Expires: {credential.expiresAt}</span>}
          </p>
        )}
        {connection.authentication === "apiKey" && (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              void actions.replaceApiKey(connection);
            }}
          >
            <fieldset disabled={disabled}>
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
        {connection.route === "chatGpt" && (
          <div class="provider-settings-sign-in">
            {signIn && (
              <p>
                Sign-in: {signIn.status}
                {signIn.error ? ` · ${signIn.error}` : ""}
              </p>
            )}
            {state.errors[`signIn:${connection.id}`] && (
              <p class="provider-settings-error">{state.errors[`signIn:${connection.id}`]}</p>
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
                disabled={disabled || (!!signIn && signInPending(signIn))}
                onClick={() => {
                  void actions.beginSignIn(connection);
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
                  disabled={disabled}
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

      <SavedModels state={state} connection={connection} disabled={disabled} />

      <div class="provider-settings-group">
        <div class="provider-settings-actions">
          <button
            type="button"
            disabled={disabled}
            onClick={() => actions.requestConfirmation("disconnect", connection)}
          >
            Disconnect
          </button>
          <button
            type="button"
            class="provider-settings-danger"
            disabled={disabled}
            onClick={() => actions.requestConfirmation("remove", connection)}
          >
            Remove locally
          </button>
        </div>
        {confirmation && confirmation.kind !== "binding" && <ConfirmChange state={state} />}
      </div>
    </>
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
