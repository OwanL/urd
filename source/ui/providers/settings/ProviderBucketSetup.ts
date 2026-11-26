import { RequestError } from "../../http/RequestError.ts";
import type { BucketSuggestions } from "../ProviderClient.ts";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../ProviderCredentials.ts";
import { providerReady } from "./ProviderConnectionStatus.ts";
import type { BucketSetup } from "./ProviderSettingsTypes.ts";

function sameConnection(first: ProviderConnection, second: ProviderConnection): boolean {
  return (
    first.id === second.id &&
    first.name === second.name &&
    first.route === second.route &&
    first.endpoint === second.endpoint &&
    first.api === second.api &&
    first.authentication === second.authentication
  );
}

export function canOfferBuckets(
  connection: ProviderConnection,
  saved: ProviderConnection | undefined,
  status: ProviderCredentialStatus | undefined,
): boolean {
  return !!saved && sameConnection(saved, connection) && providerReady(connection, status);
}

export async function offerBucketsAfterRefresh(
  connection: ProviderConnection,
  ready: Promise<boolean>,
  load: () => Promise<BucketSuggestions>,
  isCurrent: () => boolean,
  publish: (setup: BucketSetup | null) => void,
): Promise<void> {
  try {
    const refreshed = await ready;
    if (!isCurrent()) return;
    if (!refreshed) {
      publish(null);
      return;
    }
    const proposal = await load();
    if (!isCurrent()) return;
    if (sameConnection(proposal.connection, connection) && proposal.assignments.length)
      publish({ phase: "offered", proposal });
    else publish(null);
  } catch {
    if (isCurrent())
      publish({ phase: "finished", message: "Could not load bucket suggestions.", error: true });
  }
}

export async function saveBucketSuggestions(
  save: () => Promise<unknown>,
  refreshPreferences: () => Promise<void> | undefined,
  publishIfCurrent: (setup: BucketSetup) => void,
): Promise<void> {
  try {
    await save();
  } catch (error) {
    publishIfCurrent({
      phase: "finished",
      message:
        error instanceof RequestError && error.status === 409
          ? "Suggestions changed. Review your assignments in Model buckets."
          : "Could not confirm the save. Check your assignments in Model buckets.",
      error: true,
    });
    return;
  }
  await refreshPreferences();
  publishIfCurrent({ phase: "finished", message: "Model buckets saved." });
}
