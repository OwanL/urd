import type { LlmApi } from "./ProviderConnection.ts";

// Provider-supplied catalog metadata.
export interface ProviderModel {
  readonly id: string;
  readonly name: string;
  readonly contextWindow?: number | null;
  readonly api?: LlmApi | null;
}

// The last successfully fetched catalog; null means it has never been fetched.
export interface ProviderModelCatalog {
  readonly connectionId: string;
  readonly refreshedAt: string | null;
  readonly models: readonly ProviderModel[];
}
