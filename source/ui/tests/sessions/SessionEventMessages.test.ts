import assert from "node:assert/strict";
import test from "node:test";
import { SessionEvents } from "../../sessions/SessionEvents.ts";
import { encoder, waitFor } from "./SessionEventFixtures.ts";

void test("parses chunked SSE events and sends the browser registration", async (t) => {
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const calls: Array<{ url: URL; options: RequestInit | undefined }> = [];
  const connected: string[] = [];
  const streamIds: string[] = [];
  const changes: Array<[string, string]> = [];
  const statuses: Array<[string, string, string | null, boolean]> = [];
  const created: string[] = [];
  const errors: string[] = [];
  let disconnected = 0;

  const fetchMock = t.mock.method(
    globalThis,
    "fetch",
    async (input: RequestInfo | URL, options?: RequestInit) => {
      assert.ok(input instanceof URL);
      calls.push({ url: input, options });
      if (input.pathname === "/api/events") {
        const body = new ReadableStream<Uint8Array>({
          start(controller) {
            streamController = controller;
            const wire = [
              '\uFEFFevent: connected\r\ndata: {"stream":"stream-a","boot":"boot-a"}\r\n\r\n',
              'event: status\ndata: {"session":"s1","boot":"boot-a","version":4,\ndata: "status":"running","title":null}\n\n',
              'event: status\ndata: {"session":"s2","boot":"boot-a","version":1,"status":"waiting","title":"Saved 雪","hasPendingUserQuestion":true}\n\n',
              'event: status\ndata: {"session":"s2","boot":"boot-a","version":2,"status":"waiting","title":"Saved 雪","hasPendingUserQuestion":false}\n\n',
              'event: change\ndata: {"session":"s1","boot":"boot-a","version":3}\n\n',
              'event: session-created\ndata: {"session":"session-雪"}\n\n',
            ].join("");
            const bytes = encoder.encode(wire);
            const snow = bytes.indexOf(0xe9);
            controller.enqueue(bytes.slice(0, 23));
            controller.enqueue(bytes.slice(23, snow + 1));
            controller.enqueue(bytes.slice(snow + 1));
          },
        });
        return new Response(body, {
          headers: { "Content-Type": "text/event-stream; charset=utf-8" },
        });
      }
      return new Response(null, { status: 204 });
    },
  );

  const events = new SessionEvents(new URL("https://server.example/path"), {
    connected(boot, streamId) {
      connected.push(boot);
      streamIds.push(streamId);
    },
    change(session, notice) {
      changes.push([session, notice]);
      throw new Error("consumer failed");
    },
    status(session, status, title, hasPendingUserQuestion) {
      statuses.push([session, status, title, hasPendingUserQuestion]);
    },
    created(session) {
      created.push(session);
    },
    disconnected() {
      disconnected++;
    },
    error(message) {
      errors.push(message);
    },
  });
  t.after(() => events.stop());
  events.update(["s1", "s2"], ["s1"]);
  events.start();
  await waitFor(
    () =>
      connected.length === 1 &&
      created.length === 1 &&
      calls.some((call) => call.options?.method === "POST"),
  );

  assert.deepEqual(connected, ["boot-a"]);
  assert.deepEqual(streamIds, ["stream-a"]);
  assert.deepEqual(changes, [["s1", "1:boot-a:3:1"]]);
  assert.deepEqual(statuses, [
    ["s1", "running", null, false],
    ["s2", "waiting", "Saved 雪", true],
    ["s2", "waiting", "Saved 雪", false],
  ]);
  assert.deepEqual(created, ["session-雪"]);
  assert.ok(errors.some((message) => message === "consumer failed"));

  const get = calls.find((call) => call.url.pathname === "/api/events");
  assert.ok(get);
  assert.equal(get.options?.method, "GET");
  assert.equal(get.options?.cache, "no-store");
  assert.equal(get.options?.credentials, "omit");
  assert.equal(get.options?.redirect, "error");
  const getHeaders = new Headers(get.options?.headers);
  assert.equal(getHeaders.get("X-Urd-Client"), "browser");
  assert.equal(getHeaders.get("Accept"), "text/event-stream");

  const post = calls.find((call) => call.url.pathname === "/api/events/stream-a");
  assert.ok(post);
  assert.equal(post.options?.method, "POST");
  assert.deepEqual(JSON.parse(String(post.options?.body)), { open: ["s1", "s2"], watched: ["s1"] });
  const postHeaders = new Headers(post.options?.headers);
  assert.equal(postHeaders.get("X-Urd-Client"), "browser");
  assert.equal(postHeaders.get("Content-Type"), "application/json");

  events.stop();
  const getSignal = get.options?.signal;
  assert.ok(getSignal instanceof AbortSignal);
  assert.equal(getSignal.aborted, true);
  assert.equal(disconnected, 1);
  assert.equal(fetchMock.mock.callCount(), calls.length);
  assert.ok(streamController);
});

void test("delivers valid tool drafts only for watched sessions", async (t) => {
  let controller: ReadableStreamDefaultController<Uint8Array> | undefined;
  const drafts: unknown[] = [];
  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
    assert.ok(input instanceof URL);
    if (input.pathname !== "/api/events") return new Response(null, { status: 204 });
    return new Response(
      new ReadableStream<Uint8Array>({
        start(stream) {
          controller = stream;
          stream.enqueue(encoder.encode('event: connected\ndata: {"stream":"a","boot":"b"}\n\n'));
        },
      }),
      { headers: { "Content-Type": "text/event-stream" } },
    );
  });
  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change() {},
    status() {},
    created() {},
    disconnected() {},
    toolDraft(draft) {
      drafts.push(draft);
    },
  });
  t.after(() => events.stop());
  events.update(["s1", "s2"], ["s1"]);
  events.start();
  await waitFor(() => controller !== undefined);
  const draft = {
    session: "s1",
    boot: "b",
    version: 1,
    executionId: "e",
    requestId: "r",
    callId: "c",
    entryId: "i",
    name: "read",
    order: 0,
    state: "drafting",
    argumentBytes: 3,
    head: "雪",
    tail: "",
  };
  const send = (data: object) =>
    controller!.enqueue(encoder.encode(`event: tool-draft\ndata: ${JSON.stringify(data)}\n\n`));
  send({ ...draft, session: "s2" });
  send({ ...draft, state: "unknown" });
  send({ ...draft, order: -1 });
  send({ ...draft, field: 42 });
  send(draft);
  send({ ...draft, field: null });
  send({ ...draft, field: "newText" });
  send({ ...draft, state: "promoted", head: "" });
  await waitFor(() => drafts.length === 4);
  assert.deepEqual(drafts, [
    draft,
    { ...draft, field: null },
    { ...draft, field: "newText" },
    { ...draft, state: "promoted", head: "" },
  ]);
  events.update(["s1", "s2"], ["s2"]);
  send({ ...draft, state: "discarded" });
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(drafts.length, 4);
});

void test("delivers per-session browser metadata notices with a session", async (t) => {
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const browser: string[] = [];

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
    assert.ok(input instanceof URL);
    if (input.pathname === "/api/events") {
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          streamController = controller;
          controller.enqueue(
            encoder.encode('event: connected\ndata: {"stream":"stream-b","boot":"boot-b"}\n\n'),
          );
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }
    return new Response(null, { status: 204 });
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change() {},
    status() {},
    created() {},
    disconnected() {},
    browser(session) {
      browser.push(session);
    },
  });
  t.after(() => events.stop());
  events.start();

  streamController?.enqueue(
    encoder.encode(
      'event: browser\ndata: {"other":"s1"}\n\n' +
        'event: browser\ndata: {"session":"s1"}\n\n' +
        'event: browser\ndata: {"session":"session-雪"}\n\n',
    ),
  );
  // Requests without a session identity are ignored.
  await waitFor(() => browser.length === 2);
  assert.deepEqual(browser, ["s1", "session-雪"]);

  events.stop();
});

void test("delivers host-wide browser provisioning notices without a session", async (t) => {
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const provisioning: Array<[string, string | null]> = [];

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
    assert.ok(input instanceof URL);
    if (input.pathname === "/api/events") {
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          streamController = controller;
          controller.enqueue(
            encoder.encode('event: connected\ndata: {"stream":"stream-c","boot":"boot-c"}\n\n'),
          );
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }
    return new Response(null, { status: 204 });
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change() {},
    status() {},
    created() {},
    disconnected() {},
    provisioning(stage, detail) {
      provisioning.push([stage, detail]);
    },
  });
  t.after(() => events.stop());
  events.start();

  streamController?.enqueue(
    encoder.encode(
      'event: browser-provisioning\ndata: {"state":"unknown","detail":"Downloading Chromium."}\n\n' +
        'event: browser-provisioning\ndata: {"state":"downloading","detail":"Downloading Chromium."}\n\n',
    ),
  );
  // Unknown stages are ignored and the real download notice passes through with its detail.
  await waitFor(() => provisioning.length === 1);
  assert.deepEqual(provisioning, [["downloading", "Downloading Chromium."]]);

  streamController?.enqueue(
    encoder.encode('event: browser-provisioning\ndata: {"state":"ready","detail":null}\n\n'),
  );
  await waitFor(() => provisioning.length === 2);
  assert.deepEqual(provisioning, [
    ["downloading", "Downloading Chromium."],
    ["ready", null],
  ]);

  events.stop();
});
