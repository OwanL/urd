import assert from "node:assert/strict";
import test from "node:test";
import { RequestError } from "../http/RequestError.ts";
import { requestJson } from "../http/requestJson.ts";
import { startHttpServer } from "./HttpTestServer.ts";

void test("uses browser fetch options and serializes only POST bodies", async (t) => {
  const fetchMock = t.mock.method(
    globalThis,
    "fetch",
    async () =>
      new Response('{"ok":true}', {
        headers: { "Content-Type": "application/json" },
      }),
  );
  const url = new URL("https://server.example/api/test?cursor=opaque");
  const signal = new AbortController().signal;

  for (const method of ["GET", "POST"] as const) {
    const body = method === "POST" ? { text: "雪 🦉" } : undefined;
    assert.deepEqual(await requestJson(url, method, body, signal), { ok: true });
    const [sentUrl, options] = fetchMock.mock.calls.at(-1)!.arguments;
    assert.equal(sentUrl, url);
    assert.equal(options?.method, method);
    assert.equal(options?.cache, "no-store");
    assert.equal(options?.credentials, "omit");
    assert.equal(options?.redirect, "error");
    assert.equal(options?.signal, signal);
    assert.equal(options?.body, JSON.stringify(body));
    const headers = new Headers(options?.headers);
    assert.equal(headers.get("X-Urd-Client"), "browser");
    assert.equal(headers.get("Accept"), "application/json");
    assert.equal(headers.get("Content-Type"), method === "POST" ? "application/json" : null);
  }
  assert.equal(fetchMock.mock.callCount(), 2);
});

void test("accepts JSON media types case-insensitively with parameters", async (t) => {
  t.mock.method(
    globalThis,
    "fetch",
    async () =>
      new Response('{"value":7}', {
        headers: { "Content-Type": "Application/JSON; charset=utf-8" },
      }),
  );
  assert.deepEqual(await requestJson(new URL("https://server.example"), "GET"), { value: 7 });
});

for (const invalid of [
  { name: "non-JSON content type", type: "text/plain", body: "not JSON", error: TypeError },
  { name: "JSON under a non-JSON content type", type: "text/plain", body: "{}", error: TypeError },
  { name: "malformed JSON", type: "application/json", body: '{"broken":', error: SyntaxError },
  { name: "empty JSON", type: "application/json", body: "", error: SyntaxError },
]) {
  void test(`rejects successful ${invalid.name} responses`, async (t) => {
    const fetchMock = t.mock.method(
      globalThis,
      "fetch",
      async () =>
        new Response(invalid.body, {
          headers: { "Content-Type": invalid.type },
        }),
    );

    await assert.rejects(requestJson(new URL("https://server.example"), "POST", {}), invalid.error);
    assert.equal(fetchMock.mock.callCount(), 1);
  });
}

for (const failure of [
  {
    name: "JSON body",
    status: 409,
    type: "application/json",
    body: '{"reason":"conflict"}',
    expected: { reason: "conflict" },
  },
  {
    name: "text body",
    status: 503,
    type: "text/plain",
    body: "temporarily unavailable",
    expected: "temporarily unavailable",
  },
  {
    name: "malformed JSON body",
    status: 502,
    type: "application/json",
    body: '{"broken":',
    expected: '{"broken":',
  },
  { name: "empty body", status: 404, type: "text/plain", body: "", expected: "" },
]) {
  void test(`preserves HTTP status and ${failure.name}`, async (t) => {
    const fetchMock = t.mock.method(
      globalThis,
      "fetch",
      async () =>
        new Response(failure.body, {
          status: failure.status,
          headers: { "Content-Type": failure.type },
        }),
    );

    await assert.rejects(requestJson(new URL("https://server.example"), "GET"), (error) => {
      assert.ok(error instanceof RequestError);
      assert.equal(error.name, "RequestError");
      assert.equal(error.status, failure.status);
      assert.deepEqual(error.responseBody, failure.expected);
      return true;
    });
    assert.equal(fetchMock.mock.callCount(), 1);
  });
}

void test("a broken error body does not erase the received status", async (t) => {
  const response = new Response("partial", { status: 502 });
  t.mock.method(response, "text", () => Promise.reject(new Error("body disconnected")));
  const fetchMock = t.mock.method(globalThis, "fetch", () => Promise.resolve(response));

  await assert.rejects(requestJson(new URL("https://server.example"), "GET"), (error) => {
    assert.ok(error instanceof RequestError);
    assert.equal(error.status, 502);
    assert.equal(error.responseBody, undefined);
    return true;
  });
  assert.equal(fetchMock.mock.callCount(), 1);
});

void test("a redirect is rejected without resending a command", { timeout: 5_000 }, async (t) => {
  const server = await startHttpServer((_request, response) => {
    response.writeHead(307, { Location: "/redirect-target" });
    response.end();
  });
  t.after(() => server.close());

  await assert.rejects(requestJson(new URL("/command", server.origin), "POST", {}));
  assert.equal(server.requests.length, 1);
  assert.equal(server.requests[0]?.method, "POST");
});
