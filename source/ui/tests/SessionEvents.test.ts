import assert from "node:assert/strict";
import test from "node:test";
import { SessionEvents } from "../sessions/SessionEvents.ts";

const encoder = new TextEncoder();

void test("parses chunked SSE events and sends the browser registration", async (t) => {
  let streamController: ReadableStreamDefaultController<Uint8Array> | undefined;
  const calls: Array<{ url: URL; options: RequestInit | undefined }> = [];
  const connected: string[] = [];
  const streamIds: string[] = [];
  const changes: Array<[string, string]> = [];
  const statuses: Array<[string, string]> = [];
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
              'event: status\ndata: {"session":"s1","boot":"boot-a","version":4,\ndata: "status":"running"}\n\n',
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
    status(session, status) {
      statuses.push([session, status]);
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
  events.update(["s1"], ["s1"]);
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
  assert.deepEqual(statuses, [["s1", "running"]]);
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
  assert.deepEqual(JSON.parse(String(post.options?.body)), { open: ["s1"], watched: ["s1"] });
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

void test("stop aborts the stream and its independent registration request", async (t) => {
  let streamSignal: AbortSignal | undefined;
  let registrationSignal: AbortSignal | undefined;
  let markRegistrationStarted!: () => void;
  const registrationStarted = new Promise<void>((resolve) => {
    markRegistrationStarted = resolve;
  });

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL, options?: RequestInit) => {
    assert.ok(input instanceof URL);
    if (input.pathname === "/api/events") {
      streamSignal = options?.signal as AbortSignal;
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(
            encoder.encode(
              'event: connected\ndata: {"stream":"stream-stop","boot":"boot-stop"}\n\n',
            ),
          );
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }

    registrationSignal = options?.signal as AbortSignal;
    markRegistrationStarted();
    return new Promise<Response>((_resolve, reject) => {
      registrationSignal?.addEventListener(
        "abort",
        () => reject(new DOMException("Aborted", "AbortError")),
        {
          once: true,
        },
      );
    });
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change() {},
    status() {},
    created() {},
    disconnected() {},
  });
  t.after(() => events.stop());
  events.start();
  await registrationStarted;
  events.stop();

  assert.ok(streamSignal?.aborted);
  assert.ok(registrationSignal?.aborted);
});

void test("a pending old registration cannot block registration after restart", async (t) => {
  const oldRegistration = deferred<Response>();
  let getCount = 0;
  let oldRegistrationSignal: AbortSignal | undefined;
  const postPaths: string[] = [];

  t.mock.method(globalThis, "fetch", async (input: RequestInfo | URL, options?: RequestInit) => {
    assert.ok(input instanceof URL);
    if (input.pathname === "/api/events") {
      getCount++;
      const streamId = `stream-${getCount}`;
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(
            encoder.encode(
              `event: connected\ndata: {"stream":"${streamId}","boot":"boot-restart"}\n\n`,
            ),
          );
        },
      });
      return new Response(body, { headers: { "Content-Type": "text/event-stream" } });
    }

    postPaths.push(input.pathname);
    if (postPaths.length === 1) {
      oldRegistrationSignal = options?.signal as AbortSignal;
      return oldRegistration.promise;
    }
    return new Response(null, { status: 204 });
  });

  const events = new SessionEvents(new URL("https://server.example"), {
    connected() {},
    change() {},
    status() {},
    created() {},
    disconnected() {},
  });
  t.after(() => events.stop());
  events.update(["s"], ["s"]);
  events.start();
  await waitFor(() => postPaths.length === 1);

  events.stop();
  assert.equal(oldRegistrationSignal?.aborted, true);
  events.start();
  await waitFor(() => getCount === 2);
  await waitFor(() => postPaths.length === 2);
  oldRegistration.resolve(new Response(null, { status: 204 }));

  assert.deepEqual(postPaths, ["/api/events/stream-1", "/api/events/stream-2"]);
  events.stop();
});

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

async function waitFor(predicate: () => boolean, timeout = 2_000): Promise<void> {
  const deadline = Date.now() + timeout;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error("Timed out waiting for session event activity.");
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

async function flush(): Promise<void> {
  for (let index = 0; index < 8; index++)
    await new Promise<void>((resolve) => setImmediate(resolve));
}

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}
