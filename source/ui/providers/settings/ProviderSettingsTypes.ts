import type { BucketSuggestions, ProviderClient } from "../ProviderClient.ts";
import type {
  CustomProviderSettings,
  ProviderConnection,
  ProviderRoute,
} from "../ProviderConnection.ts";
import type { ProviderCredentialStatus, ProviderDisconnectResult } from "../ProviderCredentials.ts";
import type { ProviderModelCatalog } from "../ProviderModels.ts";
import type { ModelSettings } from "../ModelSettings.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";

export type ProviderSettingsClient = Pick<
  ProviderClient,
  | "list"
  | "getCredentials"
  | "getModels"
  | "getModelSettings"
  | "getSignIn"
  | "getBucketSuggestions"
  | "confirmBucketSuggestions"
  | "createChatGpt"
  | "createOpenAI"
  | "createCustom"
  | "updateCustom"
  | "remove"
  | "setApiKey"
  | "beginSignIn"
  | "cancelSignIn"
  | "disconnect"
  | "refreshModels"
  | "saveModelSettings"
>;

export type ConnectionDraft = CustomProviderSettings;

export interface AddDraft extends ConnectionDraft {
  readonly route: ProviderRoute;
}

export type ProviderSettingsOperation =
  | "add"
  | "save"
  | "changeBinding"
  | "remove"
  | "disconnect"
  | "replaceApiKey"
  | "beginSignIn"
  | "cancelSignIn"
  | "refreshModels"
  | "saveModelSettings";

export type ProviderSettingsNotice =
  | {
      readonly operation: ProviderSettingsOperation;
      readonly outcome: "succeeded";
      readonly disconnect: ProviderDisconnectResult | null;
    }
  | {
      readonly operation: ProviderSettingsOperation;
      readonly outcome: "rejected";
      readonly httpStatus?: number;
    }
  | {
      readonly operation: ProviderSettingsOperation;
      readonly outcome: "uncertain";
      readonly httpStatus?: number;
    };

export type ProviderCredentialState =
  | "unknown"
  | "disconnecting"
  | "noAuthentication"
  | "noCredentials"
  | "signInRequired"
  | "planAccessMissing"
  | "connected";

export interface Confirmation {
  readonly kind: "binding" | "disconnect" | "remove";
  readonly connection: ProviderConnection;
  readonly settings?: CustomProviderSettings;
}

export type BucketSetup =
  | { readonly phase: "loading" }
  | { readonly phase: "offered"; readonly proposal: BucketSuggestions }
  | { readonly phase: "saving"; readonly proposal: BucketSuggestions }
  | { readonly phase: "finished"; readonly message: string; readonly error?: boolean };

export interface ProviderSettingsSnapshot {
  readonly connections: readonly ProviderConnection[];
  readonly selectedId: string | null;
  readonly loaded: boolean;
  readonly reading: boolean;
  readonly pending: ProviderSettingsOperation | null;
  readonly notice: ProviderSettingsNotice | null;
  readonly uncertain: boolean;
  readonly connectionsReloadedAfterUncertainty: boolean;
  readonly errors: Readonly<Record<string, string>>;
  readonly credentials: Readonly<Record<string, ProviderCredentialStatus>>;
  readonly catalogs: Readonly<Record<string, ProviderModelCatalog>>;
  readonly modelSettings: Readonly<Record<string, readonly ModelSettings[]>>;
  readonly modelDrafts: Readonly<Record<string, readonly ModelSettings[]>>;
  readonly signIns: Readonly<Record<string, ProviderSignIn>>;
  readonly blockedSignIns: Readonly<Record<string, boolean>>;
  readonly drafts: Readonly<Record<string, ConnectionDraft>>;
  readonly add: AddDraft;
  readonly apiKey: string;
  readonly confirmation: Confirmation | null;
  readonly bucketSetup: BucketSetup | null;
}
