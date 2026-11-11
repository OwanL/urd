import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { ProviderModelCatalog } from "../providers/ProviderModels.ts";
import type { ModelSettings } from "../providers/ModelSettings.ts";
import { startHttpServer } from "./HttpTestServer.ts";

const connectionId = "50000000-0000-0000-0000-000000000005";
const connections: readonly ProviderConnection[] = [
  {
    id: connectionId,
    name: "Custom connection 雪",
    route: "custom",
    api: "openAIChatCompletions",
    endpoint: "https://provider.example/v1/",
    authentication: "apiKey",
  },
  {
    id: "60000000-0000-0000-0000-000000000006",
    name: "OpenAI",
    route: "openAI",
    api: "openAIResponses",
    endpoint: "https://api.openai.com/v1/",
    authentication: "none",
  },
  {
    id: "70000000-0000-0000-0000-000000000007",
    name: "ChatGPT",
    route: "chatGpt",
    api: "openAIResponses",
    endpoint: "https://provider.example/chat/",
    authentication: "oAuth",
  },
];
const cached: ProviderModelCatalog = { connectionId, refreshedAt: null, models: [] };
const refreshed: ProviderModelCatalog = {
  connectionId,
  refreshedAt: "2026-11-08T10:30:00+00:00",
  models: [{ id: "provider/model 雪", name: "Display name 雪", contextWindow: 32_768 }],
};

const modelSettings: ModelSettings = {
  connectionId,
  modelId: "opaque-model 雪",
  family: "anthropic",
  supportsImages: true,
  contextWindow: null,
};

const mappings = [
  {
    name: "list connections",
    call: (client: ProviderClient) => client.list(),
    method: "GET",
    path: "/api/providers",
    response: connections,
  },
  {
    name: "read the cached catalog without discovery",
    call: (client: ProviderClient) => client.getModels(connectionId),
    method: "GET",
    path: `/api/providers/${connectionId}/models`,
    response: cached,
  },
  {
    name: "explicitly refresh the catalog",
    call: (client: ProviderClient) => client.refreshModels(connectionId),
    method: "POST",
    path: `/api/providers/${connectionId}/models/refresh`,
    response: refreshed,
  },
  {
    name: "read explicit model settings without discovery",
    call: (client: ProviderClient) => client.getModelSettings(connectionId),
    method: "GET",
    path: `/api/providers/${connectionId}/model-settings`,
    response: [modelSettings],
  },
  {
    name: "save the complete model settings record",
    call: (client: ProviderClient) => client.saveModelSettings(modelSettings),
    method: "POST",
    path: `/api/providers/${connectionId}/model-settings`,
    body: modelSettings,
    response: modelSettings,
  },
];

for (const mapping of mappings) {
  void test(mapping.name, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
      response.end(JSON.stringify(mapping.response));
    });
    t.after(() => server.close());

    const baseUrl = new URL("/ignored/path?ignored=query", server.origin);
    const client = new ProviderClient(baseUrl);
    assert.deepEqual(await mapping.call(client), mapping.response);
    assert.equal(server.requests.length, 1);
    const request = server.requests[0];
    assert.ok(request);
    assert.equal(request.method, mapping.method);
    assert.equal(request.url, mapping.path);
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    assert.equal(request.headers.cookie, undefined);
    assert.equal(
      request.headers["content-type"],
      mapping.method === "POST" ? "application/json" : undefined,
    );
    assert.equal(
      request.body,
      mapping.method === "POST" ? JSON.stringify("body" in mapping ? mapping.body : {}) : "",
    );
  });
}

void test("construction does no I/O", (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async () => new Response("null"));
  new ProviderClient(new URL("https://server.example"));
  assert.equal(fetchMock.mock.callCount(), 0);
});

void test("connection IDs remain a single encoded path segment", { timeout: 5_000 }, async (t) => {
  const server = await startHttpServer((_request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify(cached));
  });
  t.after(() => server.close());
  const client = new ProviderClient(server.origin);
  const id = "opaque /?#%雪";

  await client.getModels(id);
  await client.refreshModels(id);
  await client.getModelSettings(id);
  await client.saveModelSettings({ ...modelSettings, connectionId: id });
  assert.deepEqual(
    server.requests.map((request) => request.url),
    [
      `/api/providers/${encodeURIComponent(id)}/models`,
      `/api/providers/${encodeURIComponent(id)}/models/refresh`,
      `/api/providers/${encodeURIComponent(id)}/model-settings`,
      `/api/providers/${encodeURIComponent(id)}/model-settings`,
    ],
  );
});

const reads: {
  name: string;
  call: (client: ProviderClient, signal: AbortSignal) => Promise<unknown>;
}[] = [
  { name: "list", call: (client, signal) => client.list(signal) },
  {
    name: "cached catalog",
    call: (client, signal) => client.getModels(connectionId, signal),
  },
  {
    name: "model settings",
    call: (client, signal) => client.getModelSettings(connectionId, signal),
  },
];
for (const read of reads) {
  void test(`cancelling ${read.name} never refreshes`, { timeout: 5_000 }, async (t) => {
    let received!: () => void;
    const requestReceived = new Promise<void>((resolve) => {
      received = resolve;
    });
    const server = await startHttpServer(() => received());
    t.after(() => server.close());
    const controller = new AbortController();
    t.after(() => controller.abort());
    const pending = read.call(new ProviderClient(server.origin), controller.signal);
    const rejected = assert.rejects(pending, { name: "AbortError" });
    await requestReceived;
    controller.abort();
    await rejected;

    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0]?.method, "GET");
  });
}

void test("provider errors propagate with their status and body", { timeout: 5_000 }, async (t) => {
  const failure = { error: "connectionChanged" };
  const server = await startHttpServer((_request, response) => {
    response.writeHead(503, { "Content-Type": "application/json" });
    response.end(JSON.stringify(failure));
  });
  t.after(() => server.close());

  await assert.rejects(new ProviderClient(server.origin).refreshModels(connectionId), (error) => {
    assert.ok(error instanceof RequestError);
    assert.equal(error.status, 503);
    assert.deepEqual(error.responseBody, failure);
    return true;
  });
  assert.equal(server.requests.length, 1);
});

for (const phase of ["request", "acknowledgement"] as const) {
  void test(
    `catalog refresh is not retried after ${phase} disconnect`,
    { timeout: 5_000 },
    async (t) => {
      const server = await startHttpServer((_request, response) => {
        if (phase === "request") {
          response.destroy();
          return;
        }
        response.writeHead(200, { "Content-Type": "application/json", "Content-Length": "1000" });
        response.flushHeaders();
        response.write('{"partial":', () => response.destroy());
      });
      t.after(() => server.close());

      await assert.rejects(new ProviderClient(server.origin).refreshModels(connectionId));
      assert.equal(server.requests.length, 1);
      assert.equal(server.requests[0]?.method, "POST");
    },
  );
}
