import { requestJson } from "../http/Requests.ts";
import type { McpConnectRequest } from "./McpConnectRequest.ts";
import type { McpOperation, McpServer } from "./McpServer.ts";

// Settings controller commands operate through this boundary; the client is the HTTP read/write half.
export interface McpSettingsClient {
  list(): Promise<McpServer[]>;
  connect(request: McpConnectRequest): Promise<{ id: string }>;
  reconnect(serverId: string): Promise<{ id: string }>;
  getOperation(operationId: string): Promise<McpOperation>;
  cancelOperation(operationId: string): Promise<void>;
  remove(serverId: string): Promise<void>;
}

// MCP routes are local-only and never discover anything on a read.
export class McpClient implements McpSettingsClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  list(): Promise<McpServer[]> {
    return requestJson(this.url("/mcp-servers"), "GET");
  }

  connect(request: McpConnectRequest): Promise<{ id: string }> {
    return requestJson(this.url("/mcp-servers"), "POST", request);
  }

  reconnect(serverId: string): Promise<{ id: string }> {
    return requestJson(
      this.url(`/mcp-servers/${encodeURIComponent(serverId)}/reconnect`),
      "POST",
      {},
    );
  }

  getOperation(operationId: string): Promise<McpOperation> {
    return requestJson(this.url(`/mcp-operations/${encodeURIComponent(operationId)}`), "GET");
  }

  async cancelOperation(operationId: string): Promise<void> {
    await requestJson<unknown>(
      this.url(`/mcp-operations/${encodeURIComponent(operationId)}`),
      "DELETE",
    );
  }

  async remove(serverId: string): Promise<void> {
    await requestJson<unknown>(this.url(`/mcp-servers/${encodeURIComponent(serverId)}`), "DELETE");
  }

  private url(path: string): URL {
    return new URL(`/api${path}`, this.origin);
  }
}
