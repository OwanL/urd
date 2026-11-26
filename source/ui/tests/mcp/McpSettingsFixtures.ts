import type { McpSettingsClient } from "../../mcp/McpClient.ts";
import { McpSettingsController } from "../../mcp/McpSettingsController.ts";
import type { McpOperation, McpServer } from "../../mcp/McpServer.ts";

export const savedServer: McpServer = {
  id: "one",
  name: "Work",
  url: "https://mcp.example/v1",
  authentication: "automatic",
  tools: ["search"],
};

export const connecting: McpOperation = {
  state: "connecting",
  authorizationUrl: null,
  message: null,
};

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return { promise, resolve, reject };
}

export async function flush() {
  await new Promise<void>((resolve) => setImmediate(resolve));
}

function unsupported(operation: keyof McpSettingsClient) {
  return async () => {
    throw new Error(`Unexpected McpClient operation: ${operation}`);
  };
}

export function setup(overrides: Partial<McpSettingsClient> = {}) {
  const client: McpSettingsClient = {
    list: async () => [savedServer],
    connect: unsupported("connect"),
    reconnect: unsupported("reconnect"),
    getOperation: unsupported("getOperation"),
    cancelOperation: unsupported("cancelOperation"),
    remove: unsupported("remove"),
    ...overrides,
  };
  const state = new McpSettingsController(client);
  let notifications = 0;
  state.subscribe(() => notifications++);
  return { client, state, notifications: () => notifications };
}
