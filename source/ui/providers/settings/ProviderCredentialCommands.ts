import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../ProviderCredentials.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";
import { credentialState, signInPending } from "./ProviderConnectionStatus.ts";
import type { RunProviderCommand } from "./ProviderSettingsCommand.ts";
import type { ProviderSettingsClient, ProviderSettingsSnapshot } from "./ProviderSettingsTypes.ts";

type CredentialState = Pick<ProviderSettingsSnapshot, "apiKey" | "credentials" | "signIns">;

// Submits credential changes without owning sign-in tracking or setup-view lifetime.
export function credentialCommands(
  client: Pick<ProviderSettingsClient, "setApiKey" | "beginSignIn" | "cancelSignIn">,
  command: RunProviderCommand,
  snapshot: () => CredentialState,
  publish: (update: Partial<Omit<CredentialState, "apiKey">>) => void,
) {
  function storeCredentials(status: ProviderCredentialStatus): void {
    publish({ credentials: { ...snapshot().credentials, [status.connectionId]: status } });
  }

  function storeSignIn(signIn: ProviderSignIn): void {
    publish({ signIns: { ...snapshot().signIns, [signIn.connectionId]: signIn } });
  }

  async function replaceApiKey(
    connection: ProviderConnection,
    ready?: () => boolean,
  ): Promise<void> {
    const { credentials, apiKey } = snapshot();
    const initialKey = ready && !credentials[connection.id]?.hasCredentials;
    if (!apiKey) return;
    await command("replaceApiKey", () => client.setApiKey(connection, apiKey), storeCredentials, {
      afterSuccess: (status) =>
        Boolean(initialKey && credentialState(connection, status) === "connected" && ready()),
    });
  }

  async function beginSignIn(
    connection: ProviderConnection,
    trackSetup?: (id: string) => void,
    started?: (signIn: ProviderSignIn) => void,
  ): Promise<void> {
    const { credentials, signIns } = snapshot();
    const initialSignIn = trackSetup && !credentials[connection.id]?.hasCredentials;
    if (
      Object.values(signIns).some(
        (item) => item.connectionId === connection.id && signInPending(item),
      )
    )
      return;
    await command(
      "beginSignIn",
      () => client.beginSignIn(connection.id),
      (signIn) => {
        storeSignIn(signIn);
        started?.(signIn);
      },
      {
        afterSuccess: (signIn) => {
          if (!initialSignIn || !signInPending(signIn)) return false;
          trackSetup(signIn.id);
          return true; // Discovery waits for successful authentication.
        },
      },
    );
  }

  async function cancelSignIn(
    signIn: ProviderSignIn,
    forgetSetup: (id: string) => void,
  ): Promise<void> {
    await command(
      "cancelSignIn",
      () => client.cancelSignIn(signIn.id),
      (updated) => {
        forgetSetup(signIn.id);
        storeSignIn(updated);
      },
    );
  }

  return { replaceApiKey, beginSignIn, cancelSignIn };
}
