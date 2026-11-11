// Provider-supplied identifiers and names, without inferred capabilities or pricing.
export interface ProviderModel {
  readonly id: string;
  readonly name: string;
  readonly contextWindow?: number | null;
}

// The last successfully fetched catalog; null means it has never been fetched.
export interface ProviderModelCatalog {
  readonly connectionId: string;
  readonly refreshedAt: string | null;
  readonly models: readonly ProviderModel[];
}
