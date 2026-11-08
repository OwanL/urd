import { requestEmpty, requestJson } from "../http/requestJson.ts";
import type { CustomProviderSettings, ProviderConnection } from "./ProviderConnection.ts";
import type { ProviderCredentialStatus, ProviderDisconnectResult } from "./ProviderCredentials.ts";
import type { ProviderSignIn } from "./ProviderSignIn.ts";
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

  get(connectionId: string, signal?: AbortSignal): Promise<ProviderConnection> {
    return requestJson(this.url(`/${encodeURIComponent(connectionId)}`), "GET", undefined, signal);
  }

  createChatGpt(name: string): Promise<ProviderConnection> {
    return requestJson(this.url("/chatgpt"), "POST", { name });
  }

  createOpenAI(name: string): Promise<ProviderConnection> {
    return requestJson(this.url("/openai"), "POST", { name });
  }

  createCustom(settings: CustomProviderSettings): Promise<ProviderConnection> {
    return requestJson(this.url("/custom"), "POST", settings);
  }

  rename(connectionId: string, name: string): Promise<ProviderConnection> {
    return requestJson(this.url(`/${encodeURIComponent(connectionId)}/rename`), "POST", { name });
  }

  updateCustom(
    connectionId: string,
    settings: CustomProviderSettings,
  ): Promise<ProviderConnection> {
    return requestJson(
      this.url(`/${encodeURIComponent(connectionId)}/custom-settings`),
      "POST",
      settings,
    );
  }

  remove(connectionId: string): Promise<void> {
    return requestEmpty(this.url(`/${encodeURIComponent(connectionId)}/remove`), "POST", {});
  }

  getCredentials(connectionId: string, signal?: AbortSignal): Promise<ProviderCredentialStatus> {
    return requestJson(
      this.url(`/${encodeURIComponent(connectionId)}/credentials`),
      "GET",
      undefined,
      signal,
    );
  }

  setApiKey(connection: ProviderConnection, apiKey: string): Promise<ProviderCredentialStatus> {
    return requestJson(this.url(`/${encodeURIComponent(connection.id)}/api-key`), "POST", {
      connection,
      apiKey,
    });
  }

  beginSignIn(connectionId: string): Promise<ProviderSignIn> {
    return requestJson(this.url(`/${encodeURIComponent(connectionId)}/sign-in`), "POST", {});
  }

  getSignIn(signInId: string, signal?: AbortSignal): Promise<ProviderSignIn> {
    return requestJson(
      this.url(`/sign-ins/${encodeURIComponent(signInId)}`),
      "GET",
      undefined,
      signal,
    );
  }

  cancelSignIn(signInId: string): Promise<ProviderSignIn> {
    return requestJson(this.url(`/sign-ins/${encodeURIComponent(signInId)}/cancel`), "POST", {});
  }

  disconnect(connectionId: string): Promise<ProviderDisconnectResult> {
    return requestJson(this.url(`/${encodeURIComponent(connectionId)}/disconnect`), "POST", {});
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
