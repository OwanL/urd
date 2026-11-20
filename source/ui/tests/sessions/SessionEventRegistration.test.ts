import assert from "node:assert/strict";
import test from "node:test";
import { SessionEvents } from "../../sessions/SessionEvents.ts";
import { encoder, waitFor, flush, deferred } from "./SessionEventFixtures.ts";

void test(
  "coalesces registration updates and ignores changes after unwatching",
  { timeout: 2_000 },
  async (t) => {
    const heldRegistration = deferred<void>();
    const watchedChangeDelivered = deferred<void>();
    let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
    let getCount = 0;
    let activePosts = 0;
    let maxActivePosts = 0;
    const registrations: Array<{ open: string[]; watched: string[] }> = [];
    const changes: string[] = [];

    t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL, options?: RequestInit) => {
      assert.ok(input instanceof URL);
      if (input.pathname === "/api/events") {
        getCount++;
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

      activePosts++;
      maxActivePosts = Math.max(maxActivePosts, activePosts);
      const registration = JSON.parse(String(options?.body)) as {
        open: string[];
        watched: string[];
      };
      registrations.push(registration);
      try {
        if (registration.open.includes("a")) await heldRegistration.promise;
        return new Response(null, { status: 204 });
      } finally {
        activePosts--;
      }
    });

    const events = new SessionEvents(new URL("https://server.example"), {
      connected() {},
      change(session) {
        changes.push(session);
        if (session === "b") watchedChangeDelivered.resolve();
      },
      status() {},
      created() {},
      disconnected() {},
    });
    t.after(() => events.stop());
    events.start();
    await waitFor(() => registrations.length === 1);
    events.update(["a"], ["a"]);
    await waitFor(() => registrations.length === 2);
    events.update(["a", "b"], ["b"]);
    events.update(["b"], ["b"]);
    streamController?.enqueue(
      encoder.encode(
        'event: status\ndata: {"session":"a","boot":"boot-b","version":1,"status":"running"}\n\n' +
          'event: change\ndata: {"session":"a","boot":"boot-b","version":1}\n\n',
      ),
    );
    heldRegistration.resolve();

    await waitFor(() => registrations.length === 3);
    streamController?.enqueue(
      encoder.encode(
        'event: status\ndata: {"session":"b","boot":"boot-b","version":2,"status":"running"}\n\n' +
          'event: change\ndata: {"session":"b","boot":"boot-b","version":2}\n\n',
      ),
    );
    // Delivery requires both the final registration acknowledgement and its status frame.
    await watchedChangeDelivered.promise;

    assert.deepEqual(registrations, [
      { open: [], watched: [] },
      { open: ["a"], watched: ["a"] },
      { open: ["b"], watched: ["b"] },
    ]);
    assert.deepEqual(changes, ["b"]);
    assert.equal(maxActivePosts, 1);
    assert.equal(getCount, 1);

    events.stop();
  },
);

void test("retains a change received before status until both handshake steps complete", async (t) => {
  const watchedRegistration = deferred<Response>();
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  let registrationCount = 0;
  const changes: Array<[string, string]> = [];

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL) => {
    assert.ok(input instanceof URL);
    if (input.pathname === "/api/events") {
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          streamController = controller;
          controller.enqueue(
            encoder.encode(
              'event: connected\ndata: {"stream":"stream-first","boot":"boot-first"}\n\n',
            ),
          );
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }

    registrationCount++;
    return registrationCount === 1
      ? new Response(null, { status: 204 })
      : watchedRegistration.promise;
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change(session, notice) {
      changes.push([session, notice]);
    },
    status() {},
    created() {},
    disconnected() {},
  });
  t.after(() => events.stop());
  events.start();
  await waitFor(() => registrationCount === 1);
  await flush();

  events.update(["a"], ["a"]);
  await waitFor(() => registrationCount === 2);
  const emit = (event: string, data: string) => {
    streamController?.enqueue(encoder.encode(`event: ${event}\ndata: ${data}\n\n`));
  };
  emit("change", '{"session":"a","boot":"boot-first","version":5}');
  await flush();
  assert.deepEqual(changes, []);

  watchedRegistration.resolve(new Response(null, { status: 204 }));
  await flush();
  assert.deepEqual(changes, []);
  emit("status", '{"session":"a","boot":"boot-first","version":5,"status":"running"}');
  await waitFor(() => changes.length === 1);

  assert.deepEqual(changes, [["a", "1:boot-first:5:2"]]);
  events.stop();
});

void test("waits for status and registration acknowledgement before releasing changes", async (t) => {
  const firstRegistration = deferred<Response>();
  const secondRegistration = deferred<Response>();
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const registrations: Array<{ open: string[]; watched: string[] }> = [];
  const changes: Array<[string, string]> = [];

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL, options?: RequestInit) => {
    assert.ok(input instanceof URL);
    if (input.pathname === "/api/events") {
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          streamController = controller;
          controller.enqueue(
            encoder.encode(
              'event: connected\ndata: {"stream":"stream-race","boot":"boot-race"}\n\n',
            ),
          );
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }

    const registration = JSON.parse(String(options?.body)) as {
      open: string[];
      watched: string[];
    };
    registrations.push(registration);
    if (registrations.length === 1) return new Response(null, { status: 204 });
    if (registrations.length === 2) return firstRegistration.promise;
    return secondRegistration.promise;
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change(session, notice) {
      changes.push([session, notice]);
    },
    status() {},
    created() {},
    disconnected() {},
  });
  t.after(() => events.stop());
  events.start();
  await waitFor(() => registrations.length === 1);
  await flush();

  events.update(["a"], ["a"]);
  await waitFor(() => registrations.length === 2);
  const emit = (event: string, data: string) => {
    streamController?.enqueue(encoder.encode(`event: ${event}\ndata: ${data}\n\n`));
  };

  emit("change", '{"session":"a","boot":"boot-race","version":5}');
  await flush();
  assert.deepEqual(changes, []);

  emit("status", '{"session":"a","boot":"boot-race","version":5,"status":"running"}');
  await flush();
  assert.deepEqual(changes, []);

  emit("change", '{"session":"a","boot":"boot-race","version":4}');
  emit("change", '{"session":"a","boot":"boot-race","version":5}');
  await flush();
  assert.deepEqual(changes, []);

  events.update(["b"], ["b"]);
  events.update(["a"], ["a"]);
  firstRegistration.resolve(new Response(null, { status: 204 }));
  await waitFor(() => registrations.length === 3 && changes.length === 1);
  assert.deepEqual(changes, [["a", "1:boot-race:5:2"]]);

  emit("change", '{"session":"a","boot":"boot-race","version":5}');
  await flush();
  assert.equal(changes.length, 1);
  emit("status", '{"session":"a","boot":"boot-race","version":5,"status":"running"}');
  emit("change", '{"session":"a","boot":"boot-race","version":5}');
  await flush();
  assert.equal(changes.length, 1);

  secondRegistration.resolve(new Response(null, { status: 204 }));
  await waitFor(() => changes.length === 2);
  assert.deepEqual(changes[1], ["a", "1:boot-race:5:3"]);
  assert.deepEqual(registrations, [
    { open: [], watched: [] },
    { open: ["a"], watched: ["a"] },
    { open: ["a"], watched: ["a"] },
  ]);

  events.stop();
});
