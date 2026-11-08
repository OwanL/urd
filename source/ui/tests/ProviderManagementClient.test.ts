import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { requestJson } from "../http/requestJson.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type {
  CustomProviderSettings,
  ProviderConnection,
} from "../providers/ProviderConnection.ts";
import type {
  ProviderCredentialStatus,
  ProviderDisconnectResult,
} from "../providers/ProviderCredentials.ts";
import type { ProviderSignIn, ProviderSignInStatus } from "../providers/ProviderSignIn.ts";
import { startHttpServer } from "./HttpTestServer.ts";

// Opaque values also exercise single-segment encoding on every addressed route.
const connectionId = "connection /?#%雪";
const signInId = "attempt /?#%雪";
const connection: ProviderConnection = {
  id: connectionId,
  name: "Custom 雪",
  route: "custom",
  api: "openAIResponses",
  endpoint: "https://provider.example/prefix/v1/",
  authentication: "apiKey",
};
const settings: CustomProviderSettings = {
  name: "Updated 雪",
  api: "openAIChatCompletions",
  endpoint: "http://localhost:11434/v1/",
  authentication: "none",
};
const credentials: ProviderCredentialStatus = {
  connectionId,
  hasCredentials: true,
  requiresSignIn: false,
  accountLabel: null,
  expiresAt: null,
  planAccessGranted: null,
  isDisconnecting: false,
};
const signIn: ProviderSignIn = {
  id: signInId,
  connectionId,
  status: "pending",
  authorizationUrl: "https://provider.example/authorize?state=public-state",
  expiresAt: "2026-11-08T12:00:00+00:00",
  error: null,
};
const disconnected: ProviderDisconnectResult = {
  connectionId,
  localCredentialsCleared: true,
  remoteRevocationConfirmed: null,
};
const apiKey = "test-only-secret";
const connectionPath = `/${encodeURIComponent(connectionId)}`;
const signInPath = `/sign-ins/${encodeURIComponent(signInId)}`;
const mutations: {
  name: string;
  call: (client: ProviderClient) => Promise<unknown>;
  path: string;
  body: unknown;
  response: unknown;
  status?: number;
}[] = [
  {
    name: "create ChatGPT",
    call: (c) => c.createChatGpt("ChatGPT 雪"),
    path: "/chatgpt",
    body: { name: "ChatGPT 雪" },
    response: { ...connection, route: "chatGpt", authentication: "oAuth" },
    status: 201,
  },
  {
    name: "create OpenAI",
    call: (c) => c.createOpenAI("OpenAI"),
    path: "/openai",
    body: { name: "OpenAI" },
    response: { ...connection, route: "openAI" },
    status: 201,
  },
  {
    name: "create custom",
    call: (c) => c.createCustom(settings),
    path: "/custom",
    body: settings,
    response: { ...connection, ...settings },
    status: 201,
  },
  {
    name: "rename",
    call: (c) => c.rename(connectionId, "Renamed 雪"),
    path: `${connectionPath}/rename`,
    body: { name: "Renamed 雪" },
    response: { ...connection, name: "Renamed 雪" },
  },
  {
    name: "update custom",
    call: (c) => c.updateCustom(connectionId, settings),
    path: `${connectionPath}/custom-settings`,
    body: settings,
    response: { ...connection, ...settings },
  },
  {
    name: "set API key with full expected binding",
    call: (c) => c.setApiKey(connection, apiKey),
    path: `${connectionPath}/api-key`,
    body: { connection, apiKey },
    response: credentials,
  },
  {
    name: "begin sign-in",
    call: (c) => c.beginSignIn(connectionId),
    path: `${connectionPath}/sign-in`,
    body: {},
    response: signIn,
  },
  {
    name: "cancel sign-in",
    call: (c) => c.cancelSignIn(signInId),
    path: `${signInPath}/cancel`,
    body: {},
    response: { ...signIn, status: "cancelled", authorizationUrl: null },
  },
  {
    name: "disconnect",
    call: (c) => c.disconnect(connectionId),
    path: `${connectionPath}/disconnect`,
    body: {},
    response: disconnected,
  },
  {
    name: "remove",
    call: (c) => c.remove(connectionId),
    path: `${connectionPath}/remove`,
    body: {},
    response: undefined,
    status: 204,
  },
];

for (const mutation of mutations) {
  void test(
    `${mutation.name} maps its payload and acknowledgement`,
    { timeout: 5_000 },
    async (t) => {
      const server = await startHttpServer((_request, response) => {
        response.writeHead(mutation.status ?? 200, { "Content-Type": "application/json" });
        response.end(JSON.stringify(mutation.response));
      });
      t.after(() => server.close());

      assert.deepEqual(
        await mutation.call(new ProviderClient(new URL("/ignored", server.origin))),
        mutation.response,
      );
      assert.equal(server.requests.length, 1);
      const request = server.requests[0]!;
      assert.equal(request.method, "POST");
      assert.equal(request.url, `/api/providers${mutation.path}`);
      assert.deepEqual(JSON.parse(request.body), mutation.body);
      assert.equal(request.headers["content-type"], "application/json");
      assert.equal(request.headers["x-urd-client"], "browser");
    },
  );
}

const reads: {
  name: string;
  call: (client: ProviderClient, signal?: AbortSignal) => Promise<unknown>;
  path: string;
  response: unknown;
}[] = [
  {
    name: "connection",
    call: (c, signal) => c.get(connectionId, signal),
    path: connectionPath,
    response: connection,
  },
  {
    name: "credentials",
    call: (c, signal) => c.getCredentials(connectionId, signal),
    path: `${connectionPath}/credentials`,
    response: credentials,
  },
  {
    name: "sign-in",
    call: (c, signal) => c.getSignIn(signInId, signal),
    path: signInPath,
    response: signIn,
  },
];

for (const read of reads) {
  void test(`${read.name} is a cancellable offline read`, { timeout: 5_000 }, async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(read.response));
    });
    t.after(() => server.close());
    const client = new ProviderClient(server.origin);
    assert.deepEqual(await read.call(client), read.response);
    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0]?.method, "GET");
    assert.equal(server.requests[0]?.url, `/api/providers${read.path}`);
    assert.equal(server.requests[0]?.body, "");

    const controller = new AbortController();
    const fetchMock = t.mock.method(
      globalThis,
      "fetch",
      (_url: Parameters<typeof fetch>[0], options?: RequestInit) => {
        assert.equal(options?.signal, controller.signal);
        return new Promise<Response>((_resolve, reject) => {
          options?.signal?.addEventListener("abort", () => reject(controller.signal.reason), {
            once: true,
          });
        });
      },
    );
    const pending = read.call(client, controller.signal);
    const rejected = assert.rejects(pending, { name: "AbortError" });
    controller.abort();
    await rejected;
    assert.equal(fetchMock.mock.callCount(), 1);
  });
}

void test("commands have no cancellation signal and never retry lost acknowledgements", async (t) => {
  const fetchMock = t.mock.method(
    globalThis,
    "fetch",
    async (_url: Parameters<typeof fetch>[0], options?: RequestInit) => {
      assert.equal(options?.signal, undefined);
      assert.equal(options?.cache, "no-store");
      assert.equal(options?.credentials, "omit");
      assert.equal(options?.redirect, "error");
      throw new TypeError("connection lost");
    },
  );
  const client = new ProviderClient(new URL("https://server.example"));
  for (const mutation of mutations) {
    const before = fetchMock.mock.callCount();
    await assert.rejects(mutation.call(client), /connection lost/);
    assert.equal(fetchMock.mock.callCount(), before + 1);
  }
});

void test("credential and sign-in reads expose only secret-free wire status", async (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async (url: Parameters<typeof fetch>[0]) => {
    const path = new URL(String(url)).pathname;
    return Response.json(
      path.endsWith("/credentials") || path.endsWith("/api-key") ? credentials : signIn,
    );
  });
  const client = new ProviderClient(new URL("https://server.example"));
  assert.deepEqual(await client.setApiKey(connection, apiKey), credentials);
  const status = await client.getCredentials(connectionId);
  assert.deepEqual(status, credentials);
  assert.equal(JSON.stringify(status).includes(apiKey), false);
  const statuses: readonly ProviderSignInStatus[] = [
    "pending",
    "completing",
    "completed",
    "cancelled",
    "expired",
    "failed",
  ];
  for (const state of statuses) {
    fetchMock.mock.mockImplementation(async () =>
      Response.json({ ...signIn, status: state, authorizationUrl: null }),
    );
    const attempt = await client.getSignIn(signInId);
    assert.equal(attempt.status, state);
    assert.equal(attempt.authorizationUrl, null);
    assert.equal(JSON.stringify(attempt).includes(apiKey), false);
  }
  for (const call of fetchMock.mock.calls.slice(1))
    assert.equal(call.arguments[1]?.body, undefined);
});

void test("removal accepts 204 without relaxing JSON-only acknowledgements", async (t) => {
  const fetchMock = t.mock.method(
    globalThis,
    "fetch",
    async () => new Response(null, { status: 204 }),
  );
  const origin = new URL("https://server.example");
  const client = new ProviderClient(origin);
  assert.equal(await client.remove(connectionId), undefined);
  await assert.rejects(client.rename(connectionId, "New name"), TypeError);
  await assert.rejects(requestJson(origin, "GET"), TypeError);

  fetchMock.mock.mockImplementation(
    async () =>
      new Response(null, { status: 204, headers: { "Content-Type": "application/json" } }),
  );
  await assert.rejects(requestJson(origin, "GET"), SyntaxError);
  fetchMock.mock.mockImplementation(async () => Response.json({}));
  await assert.rejects(client.remove(connectionId), TypeError);
});

for (const status of [400, 404, 409, 415, 422, 502, 503, 504]) {
  void test(`management and removal preserve HTTP ${status}`, async (t) => {
    const failure = { error: "connectionChanged" };
    const fetchMock = t.mock.method(globalThis, "fetch", async () =>
      Response.json(failure, { status }),
    );
    const client = new ProviderClient(new URL("https://server.example"));
    for (const call of [
      () => client.setApiKey(connection, apiKey),
      () => client.remove(connectionId),
    ]) {
      await assert.rejects(call(), (error) => {
        assert.ok(error instanceof RequestError);
        assert.equal(error.status, status);
        assert.deepEqual(error.responseBody, failure);
        assert.equal(error.message.includes(apiKey), false);
        return true;
      });
    }
    assert.equal(fetchMock.mock.callCount(), 2);
  });
}

void test("removal preserves HTTP status when the error body disconnects", async (t) => {
  const response = new Response("partial", { status: 503 });
  t.mock.method(response, "text", async () => {
    throw new TypeError("body lost");
  });
  t.mock.method(globalThis, "fetch", async () => response);
  await assert.rejects(
    new ProviderClient(new URL("https://server.example")).remove(connectionId),
    (error) => {
      assert.ok(error instanceof RequestError);
      assert.equal(error.status, 503);
      assert.equal(error.responseBody, undefined);
      return true;
    },
  );
});
