import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { PreferencesClient } from "../preferences/PreferencesClient.ts";
import { startHttpServer } from "./HttpTestServer.ts";

void test("preferences use the browser API contract", { timeout: 5_000 }, async (t) => {
  const saved = { workingDirectory: "C:\\work\\project", compactionTriggerTokenCount: 4_096 };
  const server = await startHttpServer((request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(
      JSON.stringify(
        request.method === "GET"
          ? { workingDirectory: null, compactionTriggerTokenCount: null }
          : saved,
      ),
    );
  });
  t.after(() => server.close());

  const client = new PreferencesClient(new URL("/ignored/path", server.origin));
  assert.deepEqual(await client.get(), {
    workingDirectory: null,
    compactionTriggerTokenCount: null,
  });
  assert.deepEqual(await client.setWorkingDirectory(saved.workingDirectory), saved);
  assert.deepEqual(await client.setWorkingDirectory(null), saved);
  assert.deepEqual(await client.setCompactionTriggerTokenCount(4_096), saved);
  assert.deepEqual(await client.setCompactionTriggerTokenCount(null), saved);

  assert.deepEqual(
    server.requests.map(({ method, url, body }) => ({ method, url, body })),
    [
      { method: "GET", url: "/api/preferences", body: "" },
      {
        method: "POST",
        url: "/api/preferences/working-directory",
        body: JSON.stringify({ workingDirectory: saved.workingDirectory }),
      },
      {
        method: "POST",
        url: "/api/preferences/working-directory",
        body: JSON.stringify({ workingDirectory: null }),
      },
      {
        method: "POST",
        url: "/api/preferences/compaction-trigger",
        body: JSON.stringify({ compactionTriggerTokenCount: 4_096 }),
      },
      {
        method: "POST",
        url: "/api/preferences/compaction-trigger",
        body: JSON.stringify({ compactionTriggerTokenCount: null }),
      },
    ],
  );
  for (const request of server.requests) {
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    assert.equal(request.headers.cookie, undefined);
    assert.equal(
      request.headers["content-type"],
      request.method === "POST" ? "application/json" : undefined,
    );
  }
});

void test("preference validation responses retain the provider error envelope", async (t) => {
  const failure = { error: "Working directory must be an absolute path." };
  const server = await startHttpServer((_request, response) => {
    response.writeHead(400, { "Content-Type": "application/json" });
    response.end(JSON.stringify(failure));
  });
  t.after(() => server.close());

  await assert.rejects(
    new PreferencesClient(server.origin).setWorkingDirectory("relative"),
    (error) => {
      assert.ok(error instanceof RequestError);
      assert.equal(error.status, 400);
      assert.deepEqual(error.responseBody, failure);
      return true;
    },
  );
});
