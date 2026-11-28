import "./provider-connection-detail.css";
import { useState } from "preact/hooks";
import type { ProviderConnection } from "../ProviderConnection.ts";
import { providerReady, signInPending } from "./ProviderConnectionStatus.ts";
import { authorizationLink } from "./ProviderSignInTab.ts";
import { copilotSignInError } from "./ProviderSettingsPresentation.ts";
import { ProviderApiKeyForm } from "./ProviderApiKeyForm.tsx";
import { ProviderModelCatalog } from "./ProviderModelCatalog.tsx";
import { ConnectionForm } from "./ConnectionForm.tsx";
import type { ProviderSettingsState } from "./useProviderSettings.ts";

export function ProviderConnectionDetail({
  state,
  connection,
  disabled,
  inSetup,
}: {
  state: ProviderSettingsState;
  connection: ProviderConnection;
  disabled: boolean;
  inSetup: boolean;
}) {
  const { actions } = state;
  const [advanced, setAdvanced] = useState(false);
  const credential = state.credentials[connection.id];
  const signIn = state.signIns[connection.id];
  const pendingSignIn = !!signIn && signInPending(signIn);
  const hasCredentials = !!credential?.hasCredentials;
  const missingPlanAccess = credential?.planAccessGranted === false;
  const connected = hasCredentials && !credential?.requiresSignIn && !missingPlanAccess;
  const showDisconnect = hasCredentials || !!credential?.isDisconnecting;
  const catalog = state.catalogs[connection.id];
  const copilot = connection.route === "gitHubCopilot";
  const oauth = copilot || connection.route === "chatGpt";
  const url = authorizationLink(signIn?.authorizationUrl ?? null);
  const signInError = copilot ? copilotSignInError(signIn?.error ?? null) : signIn?.error;
  const catalogRefreshFailed = signIn?.status === "completed" && signIn.catalogRefreshFailed;
  const ready = providerReady(connection, credential) && (!copilot || !pendingSignIn);
  const needsModels = ready && catalog && !catalog.models.length;
  const canLoadCopilotModels =
    copilot && ready && (catalogRefreshFailed || !catalog?.models.length);
  const showLoadModels = canLoadCopilotModels || (!inSetup && needsModels);
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
          <ProviderApiKeyForm
            state={state}
            connection={connection}
            disabled={disabled}
            inSetup={inSetup}
          />
        ))}
      {oauth && credential && (
        <div class="provider-settings-auth">
          {signInError && <p class="provider-settings-error">{signInError}</p>}
          {signIn &&
            !(copilot && signInError) &&
            ["expired", "failed", "cancelled"].includes(signIn.status) && (
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
          {connection.route === "chatGpt" && missingPlanAccess && !pendingSignIn && (
            <p>
              This sign-in did not grant Codex access. Sign in with a ChatGPT account that has it.
            </p>
          )}
          {pendingSignIn ? (
            <>
              {copilot && signIn.status === "pending" && signIn.userCode && (
                <label class="provider-settings-field provider-settings-device-code">
                  Code to enter on GitHub
                  <input
                    type="text"
                    value={signIn.userCode}
                    size={signIn.userCode.length}
                    readOnly
                    onFocus={(event) => event.currentTarget.select()}
                  />
                </label>
              )}
              <div class="provider-settings-actions">
                {(copilot || state.blockedSignIns[connection.id]) &&
                  signIn.status === "pending" &&
                  url && (
                    <a
                      class="provider-settings-action"
                      href={url}
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {copilot ? "Open GitHub ↗" : "Continue in browser ↗"}
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
            </>
          ) : (
            !connected && (
              <button
                type="button"
                disabled={disabled}
                onClick={() => {
                  void actions.beginSignIn(connection, inSetup);
                }}
              >
                {copilot ? "Sign in with GitHub" : "Sign in with ChatGPT"}
              </button>
            )
          )}
          {showDisconnect && disconnect}
        </div>
      )}
      {connection.authentication !== "apiKey" && !oauth && showDisconnect && disconnect}
      {connection.authentication === "apiKey" && !hasCredentials && showDisconnect && disconnect}
      {(needsModels || showLoadModels || catalogRefreshFailed) && (
        <div class="provider-settings-auth">
          {catalogRefreshFailed && (
            <p role="status" class="provider-settings-error">
              Signed in, but models could not be refreshed.
            </p>
          )}
          {showLoadModels && (
            <button
              type="button"
              disabled={disabled}
              onClick={() => {
                void actions.refreshModels(connection);
              }}
            >
              {catalog?.models.length ? "Refresh models" : "Load models"}
            </button>
          )}
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
            <ProviderModelCatalog
              state={state}
              connection={connection}
              disabled={disabled}
              inSetup={inSetup || canLoadCopilotModels}
            />
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
    </>
  );
}

function displayDate(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Unknown" : date.toLocaleString();
}
