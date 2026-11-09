import type { ProviderConnection } from "../ProviderConnection.ts";
import { credentialState, providerHostname, signInPending } from "./ProviderSettingsController.ts";
import type {
  ProviderCredentialState,
  ProviderSettingsNotice,
  ProviderSettingsOperation,
} from "./ProviderSettingsController.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";

export const providerNames = {
  chatGpt: "ChatGPT",
  openAI: "OpenAI",
  custom: "Custom",
} as const;

const credentialStateLabels: Record<ProviderCredentialState, string> = {
  unknown: "Checking…",
  disconnecting: "Disconnect incomplete · requests blocked",
  noAuthentication: "No sign-in needed",
  noCredentials: "Not connected",
  signInRequired: "Sign-in required",
  planAccessMissing: "Plan access missing",
  connected: "Connected",
};

export function connectionTitle(connection: ProviderConnection): string {
  return connection.route === "custom"
    ? providerHostname(connection.endpoint)
    : providerNames[connection.route];
}

type ConnectionStatusState = Pick<ProviderSettingsState, "credentials" | "signIns" | "errors">;

export function connectionStatus(
  state: ConnectionStatusState,
  connection: ProviderConnection,
): string {
  const signIn = state.signIns[connection.id];
  if (signIn && signInPending(signIn))
    return signIn.status === "completing" ? "Completing sign-in…" : "Signing in…";
  if (state.errors[`credentials:${connection.id}`]) return "Status unavailable";
  return credentialStateLabels[credentialState(connection, state.credentials[connection.id])];
}

export function connectionAttention(
  state: ConnectionStatusState,
  connection: ProviderConnection,
): string | null {
  const credential = credentialState(connection, state.credentials[connection.id]);
  const signIn = state.signIns[connection.id];
  const pendingSignIn = !!signIn && signInPending(signIn);
  const statusReadError = !!state.errors[`credentials:${connection.id}`];
  if (
    !pendingSignIn &&
    !statusReadError &&
    (credential === "connected" ||
      credential === "noAuthentication" ||
      (credential === "unknown" && connection.authentication === "none"))
  )
    return null;
  return connectionStatus(state, connection);
}

export const operationLabels: Record<ProviderSettingsOperation, string> = {
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

export function noticeText(notice: ProviderSettingsNotice): string | null {
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

export function confirmationText(
  kind: "binding" | "disconnect" | "remove",
  connection: ProviderConnection,
): string {
  if (kind === "binding")
    return `Change connection settings for “${connectionTitle(connection)}”? Saved credentials and models will be cleared, and active requests may stop.`;
  if (kind === "disconnect")
    return `Disconnect “${connectionTitle(connection)}”? Saved credentials will be cleared and active requests may stop. Urd will also try to revoke remote access where supported.`;
  return `Remove “${connectionTitle(connection)}” from this device? This deletes its settings and credentials and may stop active requests. Disconnect first if you also want to revoke remote access.`;
}
