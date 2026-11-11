import type { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { ProviderModelCatalog } from "../providers/ProviderModels.ts";
import type { ModelSettings } from "../providers/ModelSettings.ts";

export interface ModelOption {
  key: string;
  modelLabel: string;
  providerLabel: string;
  selection: LlmSelection;
}

export interface ModelCatalogRead {
  revision: number;
  options: ModelOption[] | null;
  error: unknown | null;
}

// Cached catalog reads and explicit discovery share option mapping and publication order.
export class WorkspaceModels {
  private revision = 0;
  private readonly client: Pick<
    ProviderClient,
    "list" | "getModels" | "getModelSettings" | "refreshModels"
  >;

  constructor(
    client: Pick<ProviderClient, "list" | "getModels" | "getModelSettings" | "refreshModels">,
  ) {
    this.client = client;
  }

  invalidate(): void {
    this.revision++;
  }

  isCurrent(revision: number): boolean {
    return this.revision === revision;
  }

  readCached(signal: AbortSignal): Promise<ModelCatalogRead> {
    return this.read(false, signal);
  }

  refresh(): Promise<ModelCatalogRead> {
    return this.read(true);
  }

  private async read(discover: boolean, signal?: AbortSignal): Promise<ModelCatalogRead> {
    const revision = ++this.revision;
    try {
      const connections = await this.client.list(signal);
      const options: ModelOption[] = [];
      for (const connection of connections) {
        if (!this.isCurrent(revision)) return { revision, options: null, error: null };
        const catalog = discover
          ? await this.client.refreshModels(connection.id)
          : await this.client.getModels(connection.id, signal);
        if (!this.isCurrent(revision)) return { revision, options: null, error: null };
        const settings = await this.client.getModelSettings(connection.id, signal);
        if (!this.isCurrent(revision)) return { revision, options: null, error: null };
        options.push(...mapModels(connection, catalog, settings));
      }
      return { revision, options, error: null };
    } catch (error) {
      return { revision, options: null, error };
    }
  }
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
        supportsImages: modelSettings?.supportsImages ?? false,
        contextWindow: modelSettings?.contextWindow ?? model.contextWindow ?? null,
        reasoningEffort: null,
      },
    };
  });
}
