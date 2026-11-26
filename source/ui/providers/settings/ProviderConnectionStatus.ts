import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../ProviderCredentials.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";
import type { ProviderCredentialState } from "./ProviderSettingsTypes.ts";

export function providerHostname(endpoint: string): string {
  try {
    return new URL(endpoint).hostname;
  } catch {
    return "";
  }
}

export function signInPending(signIn: ProviderSignIn): boolean {
  return signIn.status === "pending" || signIn.status === "completing";
}

export function credentialState(
  connection: ProviderConnection,
  status: ProviderCredentialStatus | undefined,
): ProviderCredentialState {
  if (!status) return "unknown";
  if (status.isDisconnecting) return "disconnecting";
  if (connection.authentication === "none") return "noAuthentication";
  if (!status.hasCredentials) return "noCredentials";
  if (status.requiresSignIn) return "signInRequired";
  if (status.planAccessGranted === false) return "planAccessMissing";
  return "connected";
}

export function providerReady(
  connection: ProviderConnection,
  status: ProviderCredentialStatus | undefined,
): boolean {
  if (status?.isDisconnecting) return false;
  return (
    connection.authentication === "none" || credentialState(connection, status) === "connected"
  );
}

// Setup is credential presence, not current validity, plan access or catalog discovery.
export function providerSetupState(
  connection: ProviderConnection,
  status: ProviderCredentialStatus | undefined,
): "complete" | "incomplete" | "unknown" {
  if (connection.authentication === "none") return "complete";
  if (!status) return "unknown";
  return status.hasCredentials ? "complete" : "incomplete";
}

export function matchesConnection(connection: ProviderConnection, search: string): boolean {
  const query = search.trim().toLocaleLowerCase();
  return [connection.name, connection.route, connection.endpoint].some((value) =>
    value.toLocaleLowerCase().includes(query),
  );
}
