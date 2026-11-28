import type { ProviderConnection } from "../../../providers/ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../../../providers/ProviderCredentials.ts";
import type { ProviderSignIn } from "../../../providers/ProviderSignIn.ts";
import { ProviderSettingsController } from "../../../providers/settings/ProviderSettingsController.ts";
import type { ProviderSettingsClient } from "../../../providers/settings/ProviderSettingsTypes.ts";

export const connection: ProviderConnection = {
  id: "one",
  name: "Work",
  route: "chatGpt",
  api: "openAIResponses",
  endpoint: "https://provider.example/v1/",
  authentication: "oAuth",
};
export const credential: ProviderCredentialStatus = {
  connectionId: "one",
  hasCredentials: false,
  requiresSignIn: true,
  accountLabel: null,
  expiresAt: null,
  planAccessGranted: null,
  isDisconnecting: false,
};
export const pending: ProviderSignIn = {
  id: "attempt",
  connectionId: "one",
  status: "pending",
  authorizationUrl: "https://auth.example/authorize",
  expiresAt: "2026-11-09T00:00:00Z",
  error: null,
};
export const emptyCatalog = { connectionId: "one", refreshedAt: null, models: [] } as const;

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return { promise, resolve, reject };
}

export async function flush() {
  await new Promise<void>((resolve) => setImmediate(resolve));
}

function unsupported(operation: keyof ProviderSettingsClient) {
  return async () => {
    throw new Error(`Unexpected ProviderClient operation: ${operation}`);
  };
}

const defaultClient: ProviderSettingsClient = {
  list: async () => [connection],
  getCredentials: async () => credential,
  getModels: async () => emptyCatalog,
  getModelSettings: async () => [],
  getSignIn: async () => pending,
  getBucketSuggestions: unsupported("getBucketSuggestions"),
  confirmBucketSuggestions: unsupported("confirmBucketSuggestions"),
  createChatGpt: unsupported("createChatGpt"),
  createGitHubCopilot: unsupported("createGitHubCopilot"),
  createOpenAI: unsupported("createOpenAI"),
  createCustom: unsupported("createCustom"),
  updateCustom: unsupported("updateCustom"),
  remove: unsupported("remove"),
  setApiKey: unsupported("setApiKey"),
  beginSignIn: unsupported("beginSignIn"),
  cancelSignIn: unsupported("cancelSignIn"),
  disconnect: unsupported("disconnect"),
  refreshModels: unsupported("refreshModels"),
  saveModelSettings: unsupported("saveModelSettings"),
};

export function setup(
  overrides: Partial<ProviderSettingsClient> = {},
  ready?: (connectionId: string) => Promise<boolean>,
  refreshPreferences?: () => Promise<void>,
  offerProviderBuckets = true,
) {
  let notifications = 0;
  const client: ProviderSettingsClient = { ...defaultClient, ...overrides };
  const state = new ProviderSettingsController(
    client,
    () => {
      notifications++;
    },
    ready,
    refreshPreferences,
    offerProviderBuckets,
  );
  return { client, state, notifications: () => notifications };
}
