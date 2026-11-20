import assert from "node:assert/strict";
import test from "node:test";
import { SessionClient } from "../../sessions/SessionClient.ts";
import { startHttpServer } from "../support/HttpTestServer.ts";
import { testSessionMappings, type MappingCase } from "./SessionClientMapping.ts";
import { sessionId, snapshot, page, notSentPage } from "./SessionClientFixtures.ts";

void test("session subagent provider requests preserve all, none, and explicit IDs", async (t) => {
  const path = "/api/sessions/session%2Fid/subagent-providers";
  const server = await startHttpServer((request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(request.method === "GET" ? '{"connectionIds":null}' : request.body);
  });
  t.after(() => server.close());

  const client = new SessionClient(server.origin);
  assert.deepEqual(await client.getSubagentProviders("session/id"), { connectionIds: null });
  assert.deepEqual(await client.setSubagentProviders("session/id", []), { connectionIds: [] });
  assert.deepEqual(await client.setSubagentProviders("session/id", null), { connectionIds: null });
  assert.deepEqual(await client.setSubagentProviders("session/id", ["provider"]), {
    connectionIds: ["provider"],
  });
  assert.deepEqual(
    server.requests.map(({ method, url, body }) => [method, url, body]),
    [
      ["GET", path, ""],
      ["POST", path, '{"connectionIds":[]}'],
      ["POST", path, '{"connectionIds":null}'],
      ["POST", path, '{"connectionIds":["provider"]}'],
    ],
  );
});

const mappings: MappingCase[] = [
  {
    name: "list without a cursor",
    call: (client) => client.list(),
    method: "GET",
    path: "/api/sessions",
    response: page,
  },
  {
    name: "create sends an empty JSON object and returns the ID",
    call: (client) => client.create(),
    method: "POST",
    path: "/api/sessions",
    body: {},
    response: { sessionId },
    expected: sessionId,
    status: 201,
  },
  {
    name: "snapshot",
    call: (client) => client.getSnapshot(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/snapshot`,
    response: snapshot,
  },
  {
    name: "set per-session working directory",
    call: (client) => client.setWorkingDirectory(sessionId, "C:\\workspace\\next"),
    method: "POST",
    path: `/api/sessions/${sessionId}/working-directory`,
    body: { workingDirectory: "C:\\workspace\\next" },
    response: { workingDirectory: "C:\\workspace\\next" },
    expected: "C:\\workspace\\next",
  },
  {
    name: "not-sent without a cursor",
    call: (client) => client.getNotSent(sessionId),
    method: "GET",
    path: `/api/sessions/${sessionId}/not-sent`,
    response: notSentPage,
  },
];

testSessionMappings(mappings);

void test("construction does no I/O", (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async () => new Response("null"));
  new SessionClient(new URL("https://server.example"));
  assert.equal(fetchMock.mock.callCount(), 0);
});

void test("create rejects an invalid session identity", { timeout: 5_000 }, async (t) => {
  const server = await startHttpServer((_request, response) => {
    response.writeHead(201, { "Content-Type": "application/json" });
    response.end('{"sessionId":""}');
  });
  t.after(() => server.close());

  await assert.rejects(new SessionClient(server.origin).create(), TypeError);
  assert.equal(server.requests.length, 1);
});

void test("cancelling a read never sends Stop", { timeout: 5_000 }, async (t) => {
  let received!: () => void;
  const requestReceived = new Promise<void>((resolve) => {
    received = resolve;
  });
  const server = await startHttpServer(() => received());
  t.after(() => server.close());
  const controller = new AbortController();
  t.after(() => controller.abort());
  const pending = new SessionClient(server.origin).list(undefined, controller.signal);
  const rejected = assert.rejects(pending, { name: "AbortError" });
  await requestReceived;
  controller.abort();
  await rejected;

  assert.equal(server.requests.length, 1);
  assert.equal(server.requests[0]?.method, "GET");
});
