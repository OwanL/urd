import type { ProviderClient } from "../ProviderClient.ts";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { LlmSelection } from "../LlmSelection.ts";
import type { ProviderModelCatalog } from "../ProviderModels.ts";
import type { ModelSettings } from "../ModelSettings.ts";
import { providerReady } from "../settings/ProviderConnectionStatus.ts";

export interface ModelOption {
  key: string;
  modelLabel: string;
  providerLabel: string;
  selection: LlmSelection;
}

export interface ModelCatalogRead {
  revision: number;
  options: ModelOption[] | null;
  hasProviders: boolean | null;
  providers: readonly ProviderConnection[] | null;
  providersToRefresh: readonly ProviderConnection[];
  successfulProviderIds: readonly string[];
  error: unknown | null;
}

// Cached catalog reads and discovery share option mapping and revision protection.
export class ModelOptions {
  private revision = 0;
  private readonly client: Pick<
    ProviderClient,
    "list" | "getModels" | "getModelSettings" | "refreshModels" | "getCredentials"
  >;

  constructor(
    client: Pick<
      ProviderClient,
      "list" | "getModels" | "getModelSettings" | "refreshModels" | "getCredentials"
    >,
  ) {
    this.client = client;
  }

  invalidate(): void {
    this.revision++;
  }

  isCurrent(revision: number): boolean {
    return this.revision === revision;
  }

  async readCached(signal: AbortSignal): Promise<ModelCatalogRead> {
    const revision = ++this.revision;
    let providers: readonly ProviderConnection[] | null = null;
    try {
      providers = await this.client.list(signal);
      if (!this.isCurrent(revision)) return canceledRead(revision);
      let firstError: unknown | null = null;
      const results = await Promise.all(
        providers.map(async (provider) => {
          try {
            const [catalog, settings] = await Promise.all([
              this.client.getModels(provider.id, signal),
              this.client.getModelSettings(provider.id, signal),
            ]);
            return { provider, catalog, options: mapModels(provider, catalog, settings) };
          } catch (error) {
            firstError ??= error;
            return { provider, catalog: null, options: [] };
          }
        }),
      );
      if (!this.isCurrent(revision)) return canceledRead(revision);
      return {
        revision,
        options: results.flatMap(({ options }) => options),
        hasProviders: providers.length > 0,
        providers,
        providersToRefresh: results
          .filter(({ catalog }) => catalog === null || catalog.refreshedAt === null)
          .map(({ provider }) => provider),
        successfulProviderIds: results
          .filter(({ catalog }) => catalog !== null)
          .map(({ provider }) => provider.id),
        error: firstError,
      };
    } catch (error) {
      if (!this.isCurrent(revision)) return canceledRead(revision);
      return {
        revision,
        options: null,
        hasProviders: providers === null ? null : providers.length > 0,
        providers,
        providersToRefresh: [],
        successfulProviderIds: [],
        error,
      };
    }
  }

  discover(
    providers: readonly ProviderConnection[],
    revision: number,
    signal?: AbortSignal,
  ): Promise<ModelCatalogRead> {
    return this.discoverProviders(providers, revision, signal, true);
  }

  async refresh(automatic = false): Promise<ModelCatalogRead> {
    const revision = ++this.revision;
    let providers: readonly ProviderConnection[] | null = null;
    try {
      providers = await this.client.list();
      if (!this.isCurrent(revision)) return canceledRead(revision);
      const result = await this.discoverProviders(providers, revision, undefined, automatic);
      if (!this.isCurrent(revision)) return canceledRead(revision);
      return {
        ...result,
        hasProviders: providers.length > 0,
        providers,
      };
    } catch (error) {
      if (!this.isCurrent(revision)) return canceledRead(revision);
      return {
        revision,
        options: null,
        hasProviders: providers === null ? null : providers.length > 0,
        providers,
        providersToRefresh: [],
        successfulProviderIds: [],
        error,
      };
    }
  }

  private async discoverProviders(
    providers: readonly ProviderConnection[],
    revision: number,
    signal?: AbortSignal,
    automatic = false,
  ): Promise<ModelCatalogRead> {
    if (!this.isCurrent(revision)) return canceledRead(revision);
    let firstError: unknown | null = null;
    const results = await Promise.all(
      providers.map(async (provider) => {
        try {
          if (automatic) {
            const status = await this.client.getCredentials(provider.id, signal);
            if (!this.isCurrent(revision) || signal?.aborted || !providerReady(provider, status))
              return { provider, options: [], succeeded: false };
          }
          const [catalog, settings] = await Promise.all([
            this.client.refreshModels(provider.id),
            this.client.getModelSettings(provider.id, signal),
          ]);
          return { provider, options: mapModels(provider, catalog, settings), succeeded: true };
        } catch (error) {
          firstError ??= error;
          return { provider, options: [], succeeded: false };
        }
      }),
    );
    if (!this.isCurrent(revision)) return canceledRead(revision);
    return {
      revision,
      options: results.flatMap(({ options }) => options),
      hasProviders: null,
      providers: null,
      providersToRefresh: [],
      successfulProviderIds: results
        .filter(({ succeeded }) => succeeded)
        .map(({ provider }) => provider.id),
      error: firstError,
    };
  }
}

function canceledRead(revision: number): ModelCatalogRead {
  return {
    revision,
    options: null,
    hasProviders: null,
    providers: null,
    providersToRefresh: [],
    successfulProviderIds: [],
    error: null,
  };
}

function mapModels(
  connection: ProviderConnection,
  catalog: ProviderModelCatalog,
  settings: readonly ModelSettings[],
): ModelOption[] {
  const settingsByModel = new Map(
    settings
      .filter((setting) => setting.connectionId === connection.id)
      .map((setting) => [setting.modelId, setting] as const),
  );
  return catalog.models.map((model) => {
    const modelSettings = settingsByModel.get(model.id);
    return {
      key: `${connection.id}/${model.id}`,
      modelLabel: model.name,
      providerLabel: connection.name,
      selection: {
        connection,
        modelId: model.id,
        family: modelSettings?.family ?? "other",
        supportsImages: modelSettings?.supportsImages ?? true,
        contextWindow: modelSettings?.contextWindow ?? model.contextWindow ?? null,
        reasoningEffort: "high",
      },
    };
  });
}
