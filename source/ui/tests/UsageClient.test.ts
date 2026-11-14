import assert from "node:assert/strict";
import test from "node:test";
import type { UsageSummary } from "../usage/UsageTypes.ts";
import { UsageClient } from "../usage/UsageClient.ts";
import { startHttpServer } from "./HttpTestServer.ts";

const sessionId = "10000000-0000-0000-0000-000000000001";
const summary: UsageSummary = {
  totals: {
    costUsd: 0.0004,
    requests: 2,
    estimatedRequests: 1,
    unknownRequests: 1,
    activeRequests: 1,
  },
  breakdown: [],
  history: [],
  recent: [],
};

void test(
  "session usage requests map the session and lightweight detail flag",
  { timeout: 5_000 },
  async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(summary));
    });
    t.after(() => server.close());

    const client = new UsageClient(new URL("/ignored/path", server.origin));
    assert.deepEqual(await client.getSummary({ sessionId, detail: false }), summary);
    assert.equal(server.requests.length, 1);
    const request = server.requests[0];
    assert.ok(request);
    assert.equal(request.method, "GET");
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    const url = new URL(request.url, server.origin);
    assert.equal(url.pathname, "/api/usage");
    assert.deepEqual(
      [...url.searchParams.entries()],
      [
        ["sessionId", sessionId],
        ["detail", "false"],
      ],
    );
  },
);

void test(
  "global usage requests map bounded ISO timestamps without the lightweight flag",
  { timeout: 5_000 },
  async (t) => {
    const server = await startHttpServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(summary));
    });
    t.after(() => server.close());

    const from = "2026-11-14T00:00:00.000Z";
    const to = "2026-11-14T12:00:00.000Z";
    const client = new UsageClient(new URL("/ignored/path", server.origin));
    assert.deepEqual(await client.getSummary({ from, to }), summary);
    const url = new URL(server.requests[0]!.url, server.origin);
    assert.equal(url.pathname, "/api/usage");
    assert.deepEqual(
      [...url.searchParams.entries()],
      [
        ["from", from],
        ["to", to],
      ],
    );
  },
);

void test("invalid session IDs and global ranges are rejected before transport", (t) => {
  const fetchMock = t.mock.method(globalThis, "fetch", async () => new Response("{}"));
  const client = new UsageClient(new URL("https://server.example"));
  assert.throws(() => client.getSummary({ sessionId: "not-a-guid" }), TypeError);
  assert.throws(
    () => client.getSummary({ from: "2026-11-15T00:00:00Z", to: "2026-11-14T00:00:00Z" }),
    TypeError,
  );
  assert.throws(
    () => client.getSummary({ from: "2026-10-01T00:00:00Z", to: "2026-11-14T00:00:00Z" }),
    TypeError,
  );
  assert.equal(fetchMock.mock.callCount(), 0);
});
