import assert from "node:assert/strict";
import test from "node:test";
import { SessionEvents } from "../../sessions/SessionEvents.ts";
import { encoder, waitFor, flush } from "./SessionEventFixtures.ts";

void test("reconnects after EOF and resyncs both unchanged and restarted boot versions", async (t) => {
  let getCount = 0;
  let disconnected = 0;
  let currentStreamId: string | null = null;
  const streamIds: string[] = [];
  const boots: string[] = [];
  const notices: string[] = [];

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
    assert.ok(input instanceof URL);
    if (input.pathname !== "/api/events") return new Response(null, { status: 204 });
    getCount++;
    const boot = getCount < 3 ? "boot-c" : "restarted-boot";
    const version = getCount < 3 ? 9 : 0;
    const frames = [
      `event: connected\ndata: {"stream":"stream-c-${getCount}","boot":"${boot}"}\n\n`,
      `event: status\ndata: {"session":"s","boot":"${boot}","version":${version},"status":"running"}\n\n`,
      `event: change\ndata: {"session":"s","boot":"${boot}","version":${version}}\n\n`,
    ];
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const frame of frames) controller.enqueue(encoder.encode(frame));
        if (getCount < 3) controller.close();
      },
    });
    return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected(boot, streamId) {
      boots.push(boot);
      streamIds.push(streamId);
      currentStreamId = streamId;
    },
    change(_session, notice) {
      notices.push(notice);
    },
    status() {},
    created() {},
    disconnected() {
      disconnected++;
      currentStreamId = null;
    },
  });
  events.update(["s"], ["s"]);
  events.start();
  await waitFor(() => notices.length === 3);

  assert.deepEqual(boots, ["boot-c", "boot-c", "restarted-boot"]);
  assert.deepEqual(streamIds, ["stream-c-1", "stream-c-2", "stream-c-3"]);
  assert.equal(currentStreamId, "stream-c-3");
  assert.equal(notices[0], "1:boot-c:9:1");
  assert.equal(notices[1], "2:boot-c:9:2");
  assert.equal(notices[2], "3:restarted-boot:0:3");
  assert.equal(getCount, 3);
  assert.equal(disconnected, 2);

  events.stop();
  assert.equal(disconnected, 3);
  assert.equal(currentStreamId, null);
});

for (const failure of ["network", "HTTP"] as const) {
  void test(`reports an initial ${failure} failure, recovers and stops without extra errors`, async (t) => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    let attempts = 0;
    let connected = 0;
    const errors: string[] = [];
    t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
      assert.ok(input instanceof URL);
      if (input.pathname !== "/api/events") return new Response(null, { status: 204 });
      attempts++;
      if (attempts === 1) {
        if (failure === "network") throw new TypeError("Failed to fetch");
        return new Response(null, { status: 503 });
      }
      return new Response(
        new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(
              encoder.encode('event: connected\ndata: {"stream":"recovered","boot":"boot"}\n\n'),
            );
          },
        }),
        { headers: { "Content-Type": "text/event-stream" } },
      );
    });
    const events = new SessionEvents(new URL("https://server.example"), {
      connected() {
        connected++;
      },
      change() {},
      status() {},
      created() {},
      disconnected() {},
      error(message) {
        errors.push(message);
      },
    });
    t.after(() => events.stop());
    events.start();
    await flush();
    assert.deepEqual(errors, [
      failure === "network" ? "Failed to fetch" : "Session event stream failed (HTTP 503).",
    ]);
    assert.equal(connected, 0);
    t.mock.timers.tick(250);
    await flush();
    assert.equal(connected, 1);
    assert.equal(attempts, 2);
    events.stop();
    t.mock.timers.tick(10_000);
    await flush();
    assert.equal(errors.length, 1);
    assert.equal(attempts, 2);
  });
}

void test("a stalled registration reconnects with the latest watch despite stream heartbeats", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const streams: ReadableStreamDefaultController<Uint8Array>[] = [];
  const registrations: string[][] = [];
  const changes: string[] = [];
  const errors: string[] = [];
  let disconnected = 0;
  t.mock.method(globalThis, "fetch", async (url: URL, options: RequestInit) => {
    if (url.pathname === "/api/events") {
      return new Response(
        new ReadableStream<Uint8Array>({
          start(controller) {
            streams.push(controller);
            controller.enqueue(
              encoder.encode(
                `event: connected\ndata: {"stream":"page-${streams.length}","boot":"boot"}\n\n`,
              ),
            );
          },
        }),
        { headers: { "Content-Type": "text/event-stream" } },
      );
    }
    const { watched } = JSON.parse(String(options.body)) as { watched: string[] };
    registrations.push(watched);
    if (registrations.length === 1) {
      const signal = options.signal!;
      return new Promise<Response>((_resolve, reject) => {
        signal.addEventListener("abort", () => reject(signal.reason), { once: true });
      });
    }
    return new Response(null, { status: 204 });
  });
  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change(session) {
      changes.push(session);
    },
    status() {},
    created() {},
    disconnected() {
      disconnected++;
    },
    error(message) {
      errors.push(message);
    },
  });
  t.after(() => events.stop());
  events.update(["a"], ["a"]);
  events.start();
  await flush();
  assert.deepEqual(registrations, [["a"]]);
  events.update(["a", "b"], ["b"]);
  t.mock.timers.tick(5_000);
  streams[0]!.enqueue(encoder.encode(": heartbeat\n\n"));
  await flush();
  t.mock.timers.tick(10_000);
  await flush();
  assert.equal(disconnected, 1);
  assert.deepEqual(errors, ["Session event registration timed out."]);

  t.mock.timers.tick(250);
  await flush();
  assert.deepEqual(registrations, [["a"], ["b"]]);
  streams[1]!.enqueue(
    encoder.encode(
      'event: status\ndata: {"session":"b","boot":"boot","version":1,"status":"idle"}\n\n' +
        'event: change\ndata: {"session":"b","boot":"boot","version":1}\n\n',
    ),
  );
  await flush();
  assert.deepEqual(changes, ["b"]);
});

void test("heartbeat resets the watchdog and stop cancels its retry", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let getCount = 0;
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const errors: string[] = [];
  let disconnected = 0;

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
    assert.ok(input instanceof URL);
    if (input.pathname !== "/api/events") return new Response(null, { status: 204 });
    getCount++;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        streamController = controller;
        controller.enqueue(
          encoder.encode('event: connected\ndata: {"stream":"stream-d","boot":"boot-d"}\n\n'),
        );
      },
    });
    return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change() {},
    status() {},
    created() {},
    disconnected() {
      disconnected++;
    },
    error(message) {
      errors.push(message);
    },
  });
  events.start();
  await flush();
  assert.equal(getCount, 1);

  t.mock.timers.tick(30_000);
  streamController?.enqueue(encoder.encode(": heartbeat\n\n"));
  await flush();
  t.mock.timers.tick(30_000);
  streamController?.enqueue(encoder.encode(": heartbeat\n\n"));
  await flush();
  t.mock.timers.tick(89_999);
  await flush();
  assert.deepEqual(errors, []);

  t.mock.timers.tick(1);
  await flush();
  assert.deepEqual(errors, ["Session event stream timed out without a heartbeat."]);
  assert.equal(disconnected, 1);

  events.stop();
  t.mock.timers.tick(10_000);
  await flush();
  assert.equal(getCount, 1);
  assert.equal(disconnected, 1);
});
