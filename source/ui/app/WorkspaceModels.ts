import type { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { ProviderModelCatalog } from "../providers/ProviderModels.ts";

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
  private readonly client: Pick<ProviderClient, "list" | "getModels" | "refreshModels">;

  constructor(client: Pick<ProviderClient, "list" | "getModels" | "refreshModels">) {
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
        options.push(...mapModels(connection, catalog));
      }
      return { revision, options, error: null };
    } catch (error) {
      return { revision, options: null, error };
    }
  }
}

function mapModels(connection: ProviderConnection, catalog: ProviderModelCatalog): ModelOption[] {
  return catalog.models.map((model) => ({
    key: `${connection.id}/${model.id}`,
    modelLabel: model.name,
    providerLabel: connection.name,
    selection: { connection, modelId: model.id, reasoningEffort: null },
  }));
}
