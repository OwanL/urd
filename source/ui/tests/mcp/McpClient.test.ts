import assert from "node:assert/strict";
import test from "node:test";
import { McpClient } from "../../mcp/McpClient.ts";
import type { McpConnectRequest } from "../../mcp/McpConnectRequest.ts";
import type { McpOperation, McpServer } from "../../mcp/McpServer.ts";
import { startHttpServer } from "../support/HttpTestServer.ts";

const serverId = "5e5d0dc0-4b0e-4a86-9dcd-b7f0b8d70977";
const operationId = "c34d1a90-2f19-4dcb-a4e2-e2b1ddcb7d61";
const servers: readonly McpServer[] = [
  {
    id: serverId,
    name: "Work",
    url: "https://mcp.example/v1",
    authentication: "customHeader",
    tools: ["search", "files.list"],
  },
];
const operation: McpOperation = {
  state: "signIn",
  authorizationUrl: "https://auth.example/authorize",
  message: null,
};
const request: McpConnectRequest = {
  url: "https://mcp.example/v1",
  authentication: "customHeader",
  headerName: "X-Key",
  credential: "secret",
};

const mappings: {
  name: string;
  call: (client: McpClient) => Promise<unknown>;
  method: "GET" | "POST" | "DELETE";
  path: string;
  body: string;
  response: unknown;
  value: unknown;
}[] = [
  {
    name: "list servers",
    call: (client) => client.list(),
    method: "GET",
    path: "/api/mcp-servers",
    body: "",
    response: servers,
    value: servers,
  },
  {
    name: "start a connection",
    call: (client) => client.connect(request),
    method: "POST",
    path: "/api/mcp-servers",
    body: JSON.stringify(request),
    response: { id: operationId },
    value: { id: operationId },
  },
  {
    name: "reconnect a saved server",
    call: (client) => client.reconnect(serverId),
    method: "POST",
    path: `/api/mcp-servers/${serverId}/reconnect`,
    body: "{}",
    response: { id: operationId },
    value: { id: operationId },
  },
  {
    name: "read operation state",
    call: (client) => client.getOperation(operationId),
    method: "GET",
    path: `/api/mcp-operations/${operationId}`,
    body: "",
    response: operation,
    value: operation,
  },
  {
    name: "cancel an operation",
    call: (client) => client.cancelOperation(operationId),
    method: "DELETE",
    path: `/api/mcp-operations/${operationId}`,
    body: "",
    response: {},
    value: undefined,
  },
  {
    name: "remove a saved server",
    call: (client) => client.remove(serverId),
    method: "DELETE",
    path: `/api/mcp-servers/${serverId}`,
    body: "",
    response: {},
    value: undefined,
  },
];

for (const mapping of mappings) {
  void test(mapping.name, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
      response.end(JSON.stringify(mapping.response));
    });
    t.after(() => server.close());

    const client = new McpClient(server.origin);
    assert.deepEqual(await mapping.call(client), mapping.value);
    assert.equal(server.requests.length, 1);
    const sent = server.requests[0];
    assert.ok(sent);
    assert.equal(sent.method, mapping.method);
    assert.equal(sent.url, mapping.path);
    assert.equal(sent.body, mapping.body);
    assert.equal(sent.headers["x-urd-client"], "browser");
    assert.equal(sent.headers.cookie, undefined);
  });
}

void test("construction performs no I/O", (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async () => new Response("null"));
  new McpClient(new URL("https://server.example"));
  assert.equal(fetchMock.mock.callCount(), 0);
});
