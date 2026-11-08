import { useCallback, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../../components/Icon.tsx";
import type { ProviderConnection, ProviderRoute } from "../ProviderConnection.ts";
import { ConnectionForm, providerNames } from "./ConnectionForm.tsx";
import {
  credentialState,
  matchesConnection,
  providerHostname,
  providerSetupState,
  signInPending,
} from "./ProviderSettingsController.ts";
import type {
  ProviderCredentialState,
  ProviderSettingsNotice,
  ProviderSettingsOperation,
} from "./ProviderSettingsController.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import "./provider-settings.css";

interface Presentation {
  screen: "overview" | "chooser" | "custom" | "detail";
  connectionId: string | null;
  creating: ReadonlySet<string> | null;
  returnId: string | null;
  setup?: boolean;
}

const operationLabels: Record<ProviderSettingsOperation, string> = {
  add: "Add connection",
  save: "Save connection",
  changeBinding: "Change connection binding",
  remove: "Remove local connection",
  disconnect: "Disconnect",
  replaceApiKey: "Replace API key",
  beginSignIn: "Begin sign-in",
  cancelSignIn: "Cancel sign-in",
  refreshModels: "Refresh models",
};

const credentialStateLabels: Record<ProviderCredentialState, string> = {
  unknown: "Checking…",
  disconnecting: "Disconnect incomplete · requests blocked",
  noAuthentication: "No sign-in needed",
  noCredentials: "Not connected",
  signInRequired: "Sign-in required",
  planAccessMissing: "Plan access missing",
  connected: "Connected",
};

// Settings unmounts its content when closed. Keep navigation with its command owner.
const presentations = new WeakMap<ProviderSettingsState["actions"], Presentation>();

export function ProviderSettings({ state, search }: ProviderSettingsProps) {
  const { actions } = state;
  const heading = useRef<HTMLHeadingElement>(null);
  const surface = useRef<HTMLElement>(null);
  const overviewControl = useRef<HTMLButtonElement>(null);
  const focusRequested = useRef(false);
  const previousSearch = useRef(search);
  const [presentation, setPresentation] = useState<Presentation>(
    () =>
      presentations.get(actions) ?? {
        screen: "overview",
        connectionId: null,
        creating: null,
        returnId: null,
      },
  );
  const { screen, connectionId, creating, returnId } = presentation;
  const show = useCallback(
    (next: Presentation, focus = true) => {
      focusRequested.current = focus;
      presentations.set(actions, next);
      setPresentation(next);
    },
    [actions],
  );
  const connection = state.connections.find(({ id }) => id === connectionId);

  useLayoutEffect(() => {
    if (search === previousSearch.current) return;
    previousSearch.current = search;
    if (search.trim() && screen !== "overview") {
      actions.select(null);
      show({ screen: "overview", connectionId: null, creating: null, returnId: null }, false);
    }
  }, [search, screen, actions, show]);

  useLayoutEffect(() => {
    // Only a successful create selects a new ID. Rejections and uncertain reads stay put.
    if (
      creating &&
      state.selectedId &&
      !creating.has(state.selectedId) &&
      state.connections.some((item) => item.id === state.selectedId)
    )
      show({
        screen: "detail",
        connectionId: state.selectedId,
        creating: null,
        returnId: null,
        setup: true,
      });
  }, [creating, state.selectedId, state.connections, show]);

  useLayoutEffect(() => {
    if (screen === "detail" && !connection && state.loaded && !state.pending)
      show({ screen: "overview", connectionId: null, creating: null, returnId: null });
  }, [screen, connection, state.loaded, state.pending, show]);

  useLayoutEffect(() => {
    if (!focusRequested.current) return;
    focusRequested.current = false;
    if (screen === "overview") {
      const control = overviewControl.current;
      (control && !control.disabled ? control : surface.current)?.focus();
    } else heading.current?.focus();
  }, [presentation, screen]);

  if (!state.active) return null;
  const complete = state.connections.filter(
    (item) => providerSetupState(item, state.credentials[item.id]) === "complete",
  );
  const unfinished = state.connections.filter(
    (item) => providerSetupState(item, state.credentials[item.id]) === "incomplete",
  );
  const unknown = state.connections.filter(
    (item) => providerSetupState(item, state.credentials[item.id]) === "unknown",
  );
  const visible = complete.filter((item) => matchesConnection(item, search));
  const setup = connection && providerSetupState(connection, state.credentials[connection.id]);
  const canEdit = state.pending === null && !state.uncertain && state.confirmation === null;
  const canNavigate = state.confirmation === null;
  const overview = () => {
    actions.select(null);
    show({ screen: "overview", connectionId: null, creating: null, returnId: connectionId });
  };
  const chooser = () => {
    actions.select(null);
    show({ screen: "chooser", connectionId: null, creating: null, returnId: null });
  };
  const open = (item: ProviderConnection, setup = false) => {
    actions.select(item.id);
    show({
      screen: "detail",
      connectionId: item.id,
      creating: null,
      returnId: item.id,
      setup,
    });
  };
  const backToChooser = screen === "custom" || (screen === "detail" && setup !== "complete");
  const create = () => {
    if (!canEdit) return;
    show({ ...presentation, creating: new Set(state.connections.map(({ id }) => id)) });
    actions.select(null);
    void actions.create();
  };
  const choose = (route: ProviderRoute) => {
    actions.editAdd({ route });
    if (route !== "custom") create();
    else {
      actions.select(null);
      show({ screen: "custom", connectionId: null, creating: null, returnId: null });
    }
  };
  const notice = state.notice ? noticeText(state.notice) : null;

  return (
    <section
      ref={surface}
      tabIndex={-1}
      class="provider-settings"
      aria-label="Providers"
      aria-busy={state.reading}
    >
      {screen !== "overview" && (
        <button
          type="button"
          class="provider-settings-back"
          disabled={!canNavigate}
          onClick={backToChooser ? chooser : overview}
        >
          <Icon name="chevron" />
          {backToChooser ? "Back to add provider" : "Back to providers"}
        </button>
      )}

      {screen === "overview" ? (
        <>
          <header class="provider-settings-overview-heading">
            <h3>Providers</h3>
            <button
              ref={returnId && visible.some(({ id }) => id === returnId) ? null : overviewControl}
              type="button"
              class="provider-settings-add"
              disabled={!canEdit}
              onClick={chooser}
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
                    onClick={() => open(item)}
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
      ) : (
        <>
          <header class="provider-settings-panel-heading">
            <h3 ref={heading} tabIndex={-1}>
              {screen === "chooser"
                ? "Add provider"
                : screen === "custom"
                  ? "Custom provider"
                  : connection
                    ? connectionTitle(connection)
                    : "Provider unavailable"}
            </h3>
            {connection && (
              <p class="provider-settings-secondary">{connectionStatus(state, connection)}</p>
            )}
          </header>
          {screen === "chooser" && (
            <>
              <ul class="provider-settings-connections" aria-label="Choose a provider">
                {(Object.keys(providerNames) as ProviderRoute[]).map((route) => (
                  <li key={route}>
                    <button
                      type="button"
                      class="provider-settings-connection"
                      disabled={!canEdit}
                      onClick={() => choose(route)}
                    >
                      <strong>{providerNames[route]}</strong>
                      <span class="provider-settings-secondary">
                        {route === "chatGpt"
                          ? "Account sign-in"
                          : route === "openAI"
                            ? "API key"
                            : "Local or compatible server"}
                      </span>
                      <Icon name="chevron" />
                    </button>
                  </li>
                ))}
              </ul>
              {[
                { title: "Finish setup", items: unfinished },
                { title: "Check setup", items: unknown },
              ].map(({ title, items }) =>
                items.length ? (
                  <section class="provider-settings-group" aria-label={title} key={title}>
                    <h4>{title}</h4>
                    <ul class="provider-settings-connections">
                      {items.map((item) => (
                        <li key={item.id}>
                          <button
                            type="button"
                            class="provider-settings-connection"
                            disabled={!canNavigate}
                            onClick={() => open(item, true)}
                          >
                            <strong>{connectionTitle(item)}</strong>
                            <span class="provider-settings-secondary">
                              {connectionStatus(state, item)}
                            </span>
                            <Icon name="chevron" />
                          </button>
                        </li>
                      ))}
                    </ul>
                  </section>
                ) : null,
              )}
            </>
          )}
          {screen === "custom" && (
            <ConnectionForm state={state} disabled={!canEdit} onCreate={create} />
          )}
          {screen === "detail" && connection && (
            <>
              <ConnectionManagement
                key={connection.id}
                state={state}
                connection={connection}
                disabled={!canEdit}
              />
              {presentation.setup && setup === "complete" && (
                <div class="provider-settings-actions">
                  <button type="button" disabled={!canNavigate} onClick={overview}>
                    Done
                  </button>
                </div>
              )}
            </>
          )}
        </>
      )}

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
    </section>
  );
}

interface ProviderSettingsProps {
  state: ProviderSettingsState;
  search: string;
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
  const [advanced, setAdvanced] = useState(false);
  const credential = state.credentials[connection.id];
  const signIn = state.signIns[connection.id];
  const pendingSignIn = !!signIn && signInPending(signIn);
  const hasCredentials = !!credential?.hasCredentials;
  const connected = hasCredentials && !credential?.requiresSignIn;
  const showDisconnect = hasCredentials || !!credential?.isDisconnecting;
  const catalog = state.catalogs[connection.id];
  const needsModels =
    catalog &&
    !catalog.refreshedAt &&
    !credential?.isDisconnecting &&
    credential?.planAccessGranted !== false &&
    (connected || connection.authentication === "none");
  const disconnect = (
    <button
      type="button"
      disabled={disabled}
      onClick={() => actions.requestConfirmation("disconnect", connection)}
    >
      Disconnect
    </button>
  );

  return (
    <>
      {state.errors[`credentials:${connection.id}`] && (
        <p class="provider-settings-error">{state.errors[`credentials:${connection.id}`]}</p>
      )}
      {connection.authentication === "apiKey" &&
        credential &&
        (hasCredentials ? (
          <div class="provider-settings-auth">
            {disconnect}
            <details class="provider-settings-details">
              <summary>Change API key</summary>
              <ApiKeyForm state={state} connection={connection} disabled={disabled} />
            </details>
          </div>
        ) : (
          <ApiKeyForm state={state} connection={connection} disabled={disabled} />
        ))}
      {connection.route === "chatGpt" && credential && (
        <div class="provider-settings-auth">
          {signIn?.error && <p class="provider-settings-error">{signIn.error}</p>}
          {signIn && ["expired", "failed", "cancelled"].includes(signIn.status) && (
            <p
              class={
                signIn.status === "cancelled"
                  ? "provider-settings-secondary"
                  : "provider-settings-error"
              }
            >
              {signIn.status === "expired"
                ? "Sign-in expired."
                : signIn.status === "cancelled"
                  ? "Sign-in cancelled."
                  : "Sign-in failed."}
            </p>
          )}
          {state.errors[`signIn:${connection.id}`] && (
            <p class="provider-settings-error">{state.errors[`signIn:${connection.id}`]}</p>
          )}
          {pendingSignIn ? (
            <div class="provider-settings-actions">
              {signIn.status === "pending" && authorizationLink(signIn.authorizationUrl) && (
                <a
                  class="provider-settings-action"
                  href={authorizationLink(signIn.authorizationUrl)!}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  Continue in browser ↗
                </a>
              )}
              <button
                type="button"
                disabled={disabled}
                onClick={() => {
                  void actions.cancelSignIn(signIn);
                }}
              >
                Cancel
              </button>
            </div>
          ) : (
            !connected && (
              <button
                type="button"
                disabled={disabled}
                onClick={() => {
                  void actions.beginSignIn(connection);
                }}
              >
                Sign in
              </button>
            )
          )}
          {showDisconnect && disconnect}
        </div>
      )}
      {connection.authentication !== "apiKey" &&
        connection.route !== "chatGpt" &&
        showDisconnect &&
        disconnect}
      {connection.authentication === "apiKey" && !hasCredentials && showDisconnect && disconnect}
      {needsModels && (
        <div class="provider-settings-auth">
          <button
            type="button"
            disabled={disabled}
            onClick={() => {
              void actions.refreshModels(connection);
            }}
          >
            Load models
          </button>
          {state.errors[`models:${connection.id}`] && (
            <p class="provider-settings-error">{state.errors[`models:${connection.id}`]}</p>
          )}
        </div>
      )}

      <details
        class="provider-settings-details provider-settings-advanced"
        onToggle={(event) => setAdvanced(event.currentTarget.open)}
      >
        <summary>Advanced</summary>
        {advanced && (
          <div class="provider-settings-advanced-content">
            {connection.route === "custom" && (
              <details class="provider-settings-details">
                <summary>Edit connection</summary>
                <ConnectionForm state={state} connection={connection} disabled={disabled} />
              </details>
            )}
            <SavedModels state={state} connection={connection} disabled={disabled} />
            {(credential?.accountLabel || credential?.expiresAt || pendingSignIn) && (
              <div class="provider-settings-group provider-settings-secondary">
                {credential?.accountLabel && <p>Account: {credential.accountLabel}</p>}
                {credential?.expiresAt && (
                  <p>Credentials expire: {displayDate(credential.expiresAt)}</p>
                )}
                {pendingSignIn && <p>Sign-in expires: {displayDate(signIn.expiresAt)}</p>}
              </div>
            )}
            <div class="provider-settings-group provider-settings-actions">
              {!showDisconnect && disconnect}
              <button
                type="button"
                class="provider-settings-danger"
                disabled={disabled}
                onClick={() => actions.requestConfirmation("remove", connection)}
              >
                Remove provider
              </button>
            </div>
          </div>
        )}
      </details>
      {state.confirmation?.connection.id === connection.id && <ConfirmChange state={state} />}
    </>
  );
}

function ApiKeyForm({
  state,
  connection,
  disabled,
}: {
  state: ProviderSettingsState;
  connection: ProviderConnection;
  disabled: boolean;
}) {
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void state.actions.replaceApiKey(connection);
      }}
    >
      <fieldset disabled={disabled}>
        <label class="provider-settings-field">
          API key
          <input
            type="password"
            value={state.apiKey}
            autoComplete="new-password"
            spellcheck={false}
            autoCapitalize="off"
            onInput={(event) => state.actions.setApiKey(event.currentTarget.value)}
          />
        </label>
        <p class="provider-settings-secondary">
          Stored on this device without encryption. Saved keys are never displayed.
        </p>
        <button type="submit" disabled={!state.apiKey}>
          Save key
        </button>
        {state.notice?.operation === "replaceApiKey" && state.notice.outcome === "succeeded" && (
          <p role="status">Key saved.</p>
        )}
      </fieldset>
    </form>
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

function connectionTitle(connection: ProviderConnection): string {
  return connection.route === "custom"
    ? providerHostname(connection.endpoint)
    : providerNames[connection.route];
}

function connectionStatus(state: ProviderSettingsState, connection: ProviderConnection): string {
  const signIn = state.signIns[connection.id];
  if (signIn && signInPending(signIn))
    return signIn.status === "completing" ? "Completing sign-in…" : "Signing in…";
  if (state.errors[`credentials:${connection.id}`]) return "Status unavailable";
  return credentialStateLabels[credentialState(connection, state.credentials[connection.id])];
}

function connectionAttention(
  state: ProviderSettingsState,
  connection: ProviderConnection,
): string | null {
  const status = connectionStatus(state, connection);
  const credential = credentialState(connection, state.credentials[connection.id]);
  if (
    status === credentialStateLabels[credential] &&
    (credential === "connected" ||
      credential === "noAuthentication" ||
      (credential === "unknown" && connection.authentication === "none"))
  )
    return null;
  return status;
}

function noticeText(notice: ProviderSettingsNotice): string | null {
  if (notice.outcome === "succeeded") {
    if (notice.operation !== "disconnect") return null;
    if (!notice.disconnect) return "Disconnect completed; result details unavailable.";
    const { localCredentialsCleared, remoteRevocationConfirmed } = notice.disconnect;
    const remoteRevocation =
      remoteRevocationConfirmed === null
        ? "not applicable"
        : remoteRevocationConfirmed
          ? "confirmed"
          : "not confirmed";
    return `Local credentials ${localCredentialsCleared ? "cleared" : "not confirmed cleared"}. Remote revocation: ${remoteRevocation}.`;
  }

  const label = operationLabels[notice.operation];
  const detail = notice.httpStatus === undefined ? "" : ` (HTTP ${notice.httpStatus})`;
  if (notice.outcome === "rejected") return `${label} rejected${detail}. No automatic retry.`;
  return `${label}: outcome uncertain${detail}. The server may still have accepted it. Saved state refreshes automatically, but cannot prove unfinished work stopped. Explicitly allow new commands to continue.`;
}

function displayDate(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unknown" : date.toLocaleString();
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
    return `Change connection settings for “${connectionTitle(connection)}”? Saved credentials and models will be cleared, and active requests may stop.`;
  if (kind === "disconnect")
    return `Disconnect “${connectionTitle(connection)}”? Saved credentials will be cleared and active requests may stop. Urd will also try to revoke remote access where supported.`;
  return `Remove “${connectionTitle(connection)}” from this device? This deletes its settings and credentials and may stop active requests. Disconnect first if you also want to revoke remote access.`;
}
