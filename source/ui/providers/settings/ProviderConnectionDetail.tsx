import { useState } from "preact/hooks";
import type { ProviderConnection } from "../ProviderConnection.ts";
import { signInPending } from "./ProviderSettingsController.ts";
import { ProviderApiKeyForm } from "./ProviderApiKeyForm.tsx";
import { ProviderModelCatalog } from "./ProviderModelCatalog.tsx";
import { ProviderConnectionConfirmation } from "./ProviderConnectionConfirmation.tsx";
import { ConnectionForm } from "./ConnectionForm.tsx";
import type { ProviderSettingsState } from "./useProviderSettings.ts";

export function ProviderConnectionDetail({
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
              <ProviderApiKeyForm state={state} connection={connection} disabled={disabled} />
            </details>
          </div>
        ) : (
          <ProviderApiKeyForm state={state} connection={connection} disabled={disabled} />
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
            <ProviderModelCatalog state={state} connection={connection} disabled={disabled} />
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
      {state.confirmation?.connection.id === connection.id && (
        <ProviderConnectionConfirmation state={state} />
      )}
    </>
  );
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
