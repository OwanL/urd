import assert from "node:assert/strict";
import test from "node:test";
import { DirectoryClient } from "../files/DirectoryClient.ts";
import { startHttpServer } from "./HttpTestServer.ts";

void test("directory listings use the browser API contract", { timeout: 5_000 }, async (t) => {
  const listing = {
    path: "C:\\work\\project",
    parentPath: "C:\\work",
    directories: [{ name: "src files", path: "C:\\work\\project\\src files" }],
    truncated: true,
  };
  const server = await startHttpServer((request, response) => {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(
      JSON.stringify(
        request.url === "/api/files/directories"
          ? {
              path: null,
              parentPath: null,
              directories: [{ name: "work", path: "C:\\work" }],
              truncated: false,
            }
          : listing,
      ),
    );
  });
  t.after(() => server.close());

  const client = new DirectoryClient(new URL("/ignored/path", server.origin));
  const roots = await client.list(null);
  const selected = await client.list("C:\\work\\project", "src files");

  assert.deepEqual(roots, {
    path: null,
    parentPath: null,
    directories: [{ name: "work", path: "C:\\work" }],
    truncated: false,
  });
  assert.deepEqual(selected, listing);
  assert.equal(server.requests[0]?.url, "/api/files/directories");
  const selectedUrl = new URL(server.requests[1]!.url, server.origin);
  assert.equal(selectedUrl.pathname, "/api/files/directories");
  assert.equal(selectedUrl.searchParams.get("path"), "C:\\work\\project");
  assert.equal(selectedUrl.searchParams.get("filter"), "src files");
  for (const request of server.requests) {
    assert.equal(request.method, "GET");
    assert.equal(request.headers["x-urd-client"], "browser");
    assert.equal(request.headers.accept, "application/json");
    assert.equal(request.headers.cookie, undefined);
    assert.equal(request.body, "");
  }
});
