import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import type { ProviderModelCatalog } from "../providers/ProviderModels.ts";
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
  models: [{ id: "provider/model 雪", name: "Display name 雪" }],
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
];

for (const mapping of mappings) {
  void test(mapping.name, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
      response.end(JSON.stringify(mapping.response));
    });
    t.after(() => server.close());

    assert.deepEqual(await mapping.call(new ProviderClient(server.origin)), mapping.response);
    assert.equal(server.requests.length, 1);
    const request = server.requests[0];
    assert.ok(request);
    assert.equal(request.method, mapping.method);
    assert.equal(request.url, mapping.path);
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    assert.equal(request.headers.cookie, undefined);
    assert.equal(request.body, mapping.method === "POST" ? "{}" : "");
    assert.equal(
      request.headers["content-type"],
      mapping.method === "POST" ? "application/json" : undefined,
    );
  });
}

void test("construction does no I/O; requests use exact browser transport options", async (t) => {
  const fetchMock = t.mock.method(
    globalThis,
    "fetch",
    async () => new Response("null", { headers: { "Content-Type": "application/json" } }),
  );
  const client = new ProviderClient(new URL("https://server.example/ignored/path?ignored=query"));
  assert.equal(fetchMock.mock.callCount(), 0);

  for (const mapping of mappings) {
    await mapping.call(client);
    const call = fetchMock.mock.calls.at(-1);
    assert.ok(call);
    const [url, options] = call.arguments;
    assert.ok(url instanceof URL);
    assert.equal(url.href, `https://server.example${mapping.path}`);
    assert.deepEqual(options, {
      method: mapping.method,
      headers: {
        "X-Urd-Client": "browser",
        Accept: "application/json",
        ...(mapping.method === "POST" ? { "Content-Type": "application/json" } : {}),
      },
      cache: "no-store",
      credentials: "omit",
      redirect: "error",
      ...(mapping.method === "POST" ? { body: "{}" } : {}),
    });
  }
  assert.equal(fetchMock.mock.callCount(), mappings.length);
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
  assert.deepEqual(
    server.requests.map((request) => request.url),
    [
      `/api/providers/${encodeURIComponent(id)}/models`,
      `/api/providers/${encodeURIComponent(id)}/models/refresh`,
    ],
  );
});

const reads = [
  { name: "list", call: (client: ProviderClient, signal: AbortSignal) => client.list(signal) },
  {
    name: "cached catalog",
    call: (client: ProviderClient, signal: AbortSignal) => client.getModels(connectionId, signal),
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

for (const failure of [
  {
    name: "JSON",
    status: 409,
    body: '{"error":"connectionChanged"}',
    expected: { error: "connectionChanged" },
  },
  {
    name: "text",
    status: 503,
    body: "temporarily unavailable",
    expected: "temporarily unavailable",
  },
  { name: "malformed JSON", status: 502, body: '{"broken":', expected: '{"broken":' },
  { name: "empty body", status: 404, body: "", expected: "" },
]) {
  for (const mapping of mappings) {
    void test(
      `${mapping.name} preserves ${failure.name} HTTP errors without retry`,
      { timeout: 5_000 },
      async (t) => {
        const server = await startHttpServer((_request, response) => {
          response.writeHead(failure.status, { "Content-Type": "application/json" });
          response.end(failure.body);
        });
        t.after(() => server.close());
        await assert.rejects(mapping.call(new ProviderClient(server.origin)), (error) => {
          assert.ok(error instanceof RequestError);
          assert.equal(error.name, "RequestError");
          assert.equal(error.status, failure.status);
          assert.deepEqual(error.responseBody, failure.expected);
          return true;
        });
        assert.equal(server.requests.length, 1);
      },
    );
  }
}

void test("a broken error body preserves the received HTTP status", async (t) => {
  const response = new Response("partial", { status: 502 });
  t.mock.method(response, "text", () => Promise.reject(new Error("body disconnected")));
  const fetchMock = t.mock.method(globalThis, "fetch", () => Promise.resolve(response));
  await assert.rejects(
    new ProviderClient(new URL("https://server.example")).refreshModels(connectionId),
    (error) => {
      assert.ok(error instanceof RequestError);
      assert.equal(error.status, 502);
      assert.equal(error.responseBody, undefined);
      return true;
    },
  );
  assert.equal(fetchMock.mock.callCount(), 1);
});

for (const success of [
  { name: "non-JSON", type: "text/plain", body: "not JSON" },
  { name: "JSON under a non-JSON media type", type: "text/plain", body: "{}" },
  { name: "malformed JSON", type: "application/json", body: '{"models":' },
  { name: "empty JSON", type: "application/json", body: "" },
]) {
  void test(
    `refresh rejects ${success.name} acknowledgements without retry`,
    { timeout: 5_000 },
    async (t) => {
      const server = await startHttpServer((_request, response) => {
        response.writeHead(200, { "Content-Type": success.type });
        response.end(success.body);
      });
      t.after(() => server.close());
      await assert.rejects(new ProviderClient(server.origin).refreshModels(connectionId));
      assert.equal(server.requests.length, 1);
    },
  );
}

for (const phase of ["request", "acknowledgement"] as const) {
  void test(`refresh never retries after a ${phase} disconnect`, { timeout: 5_000 }, async (t) => {
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
  });
}

void test(
  "refresh rejects redirects instead of resending the command",
  { timeout: 5_000 },
  async (t) => {
    const server = await startHttpServer((request, response) => {
      if (request.url === "/redirect-target") {
        response.writeHead(200, { "Content-Type": "application/json" });
        response.end(JSON.stringify(refreshed));
        return;
      }
      response.writeHead(307, { Location: "/redirect-target" });
      response.end();
    });
    t.after(() => server.close());
    await assert.rejects(new ProviderClient(server.origin).refreshModels(connectionId));
    assert.equal(server.requests.length, 1);
  },
);
