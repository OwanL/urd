import assert from "node:assert/strict";
import test from "node:test";
import { SessionEvents } from "../../sessions/SessionEvents.ts";
import { encoder, waitFor, deferred } from "./SessionEventFixtures.ts";

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
