import { requestJson } from "../http/requestJson.ts";
import type { ProviderConnection } from "./ProviderConnection.ts";
import type { ProviderModelCatalog } from "./ProviderModels.ts";

// Offline reads never trigger discovery; only explicit refresh fetches a new catalog.
export class ProviderClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  list(signal?: AbortSignal): Promise<readonly ProviderConnection[]> {
    return requestJson(this.url(""), "GET", undefined, signal);
  }

  getModels(connectionId: string, signal?: AbortSignal): Promise<ProviderModelCatalog> {
    return requestJson(
      this.url(`/${encodeURIComponent(connectionId)}/models`),
      "GET",
      undefined,
      signal,
    );
  }

  refreshModels(connectionId: string): Promise<ProviderModelCatalog> {
    return requestJson(this.url(`/${encodeURIComponent(connectionId)}/models/refresh`), "POST", {});
  }

  private url(path: string): URL {
    return new URL(`/api/providers${path}`, this.origin);
  }
}
