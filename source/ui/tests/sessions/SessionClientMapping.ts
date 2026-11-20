import assert from "node:assert/strict";
import test from "node:test";
import { SessionClient } from "../../sessions/SessionClient.ts";
import { startHttpServer } from "../support/HttpTestServer.ts";

export interface MappingCase {
  name: string;
  call: (client: SessionClient) => Promise<unknown>;
  method: "GET" | "POST";
  path: string;
  query?: Record<string, string>;
  body?: unknown;
  response: unknown;
  expected?: unknown;
  status?: number;
}

export function testSessionMappings(mappings: MappingCase[]) {
  for (const mapping of mappings) {
    void test(mapping.name, { timeout: 5_000 }, async (t) => {
      const server = await startHttpServer((_request, response) => {
        response.writeHead(mapping.status ?? 200, { "Content-Type": "application/json" });
        response.end(JSON.stringify(mapping.response));
      });
      t.after(() => server.close());

      const baseUrl = new URL("/ignored/path?ignored=query", server.origin);
      const client = new SessionClient(baseUrl);
      const expected = mapping.expected === undefined ? mapping.response : mapping.expected;
      assert.deepEqual(await mapping.call(client), expected);
      assert.equal(server.requests.length, 1);
      const request = server.requests[0];
      assert.ok(request);
      assert.equal(request.method, mapping.method);
      assert.equal(request.headers["x-urd-client"], "browser");
      assert.equal(request.headers.accept, "application/json");
      assert.equal(
        request.headers["content-type"],
        mapping.method === "POST" ? "application/json" : undefined,
      );
      const url = new URL(request.url, server.origin);
      assert.equal(url.pathname, mapping.path);
      assert.deepEqual(
        [...url.searchParams.entries()].sort(),
        Object.entries(mapping.query ?? {}).sort(),
      );
      assert.equal(request.body, mapping.body === undefined ? "" : JSON.stringify(mapping.body));
    });
  }
}
