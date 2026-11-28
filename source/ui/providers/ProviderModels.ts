import type { LlmApi } from "./ProviderConnection.ts";

// Provider-supplied catalog metadata with optional reference API pricing.
export interface ProviderModel {
  readonly id: string;
  readonly name: string;
  readonly contextWindow?: number | null;
  readonly api?: LlmApi | null;
  readonly price?: ProviderModelPrice | null;
}

// Reference API rates, not Copilot subscription billing.
export interface ProviderModelPrice {
  readonly inputPerMillion: number | null;
  readonly outputPerMillion: number | null;
  readonly source: string;
  readonly capturedAt: string;
}

// The last successfully fetched catalog; null means it has never been fetched.
export interface ProviderModelCatalog {
  readonly connectionId: string;
  readonly refreshedAt: string | null;
  readonly models: readonly ProviderModel[];
}
