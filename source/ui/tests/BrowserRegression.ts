import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import test from "node:test";
import { chromium, type Page } from "playwright";
import { SessionClient } from "../sessions/SessionClient.ts";
import { encodeWorkspace, newChat } from "../app/WorkspaceState.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";
import { startLocalModelServer } from "./LocalModelServer.ts";
import { ServerProcess } from "./ServerProcess.ts";

type ModelFixture = Awaited<ReturnType<typeof startLocalModelServer>>;
type FixtureConnection = Awaited<ReturnType<ModelFixture["install"]>>;
type BrowserSseEvent = { name: string; data: Record<string, unknown> };
type BrowserPushEvent = BrowserSseEvent & { at: number };
type LegacyPollResult = {
  renderedAt: number;
  positiveReadAt: number;
  negativeReadStartedAt: number;
  polls: number;
  requestCounts: { snapshot: number; history: number; content: number };
};
type BenchmarkRead = { path: string; at: number; polling: boolean };

async function installBrowserPushProbe(page: Page) {
  await page.addInitScript(() => {
    const probe = {
      streams: [] as { signal: AbortSignal | null; id: string | null }[],
      events: [] as { name: string; data: Record<string, unknown>; at: number }[],
      reads: [] as BenchmarkRead[],
    };
    (window as unknown as { __urdPushProbe: typeof probe }).__urdPushProbe = probe;
    const originalFetch = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const url = new URL(
        typeof input === "string" ? input : input instanceof URL ? input : input.url,
        location.href,
      );
      const method = (
        init?.method ?? (input instanceof Request ? input.method : "GET")
      ).toUpperCase();
      const headers = new Headers(init?.headers);
      if (input instanceof Request)
        input.headers.forEach((value, key) => {
          if (!headers.has(key)) headers.set(key, value);
        });
      const readPath = url.pathname.match(/^\/api\/sessions\/[^/]+\/(snapshot|history|content)$/);
      if (method === "GET" && readPath)
        probe.reads.push({
          path: readPath[1]!,
          at: performance.now(),
          polling: headers.get("X-Urd-Benchmark-Poll") === "350",
        });
      const response = await originalFetch(input, init);
      if (url.pathname !== "/api/events" || (init?.method ?? "GET") !== "GET") return response;

      const stream = {
        signal: init?.signal ?? (input instanceof Request ? input.signal : null),
        id: null as string | null,
      };
      probe.streams.push(stream);
      void (async () => {
        const reader = response.clone().body?.getReader();
        if (!reader) return;
        const decoder = new TextDecoder();
        let buffer = "";
        while (true) {
          const next = await reader.read();
          if (next.done) return;
          buffer += decoder.decode(next.value, { stream: true });
          let boundary = buffer.indexOf("\n\n");
          while (boundary >= 0) {
            const frame = buffer.slice(0, boundary);
            buffer = buffer.slice(boundary + 2);
            let name = "";
            let data = "";
            for (const line of frame.split(/\r?\n/)) {
              if (line.startsWith("event: ")) name = line.slice(7);
              if (line.startsWith("data: ")) data += line.slice(6);
            }
            if (name && data) {
              const value = JSON.parse(data) as Record<string, unknown>;
              if (name === "connected") stream.id = String(value.stream);
              probe.events.push({ name, data: value, at: performance.now() });
            }
            boundary = buffer.indexOf("\n\n");
          }
        }
      })().catch(() => {});
      return response;
    };
  });
}

async function waitForRenderedMarker(page: Page, marker: string): Promise<number> {
  return page.evaluate(
    (value) =>
      new Promise<number>((resolve, reject) => {
        const timer = window.setTimeout(() => {
          observer.disconnect();
          reject(new Error("The push-rendered benchmark marker did not appear."));
        }, 10_000);
        const observer = new MutationObserver(() => {
          if (
            ![...document.querySelectorAll("#chat-view .transcript .message-text")].some((node) =>
              node.textContent?.includes(value),
            )
          )
            return;
          observer.disconnect();
          clearTimeout(timer);
          requestAnimationFrame(() => resolve(performance.now()));
        });
        observer.observe(document.body, { childList: true, characterData: true, subtree: true });
      }),
    marker,
  );
}

// The current server no longer returns transcript previews in snapshots, so the historical
// polling cadence is rendered here using equivalent current snapshot/history/content reads.
async function startLegacyPollingRenderer(
  page: Page,
  sessionId: string,
  executionId: string,
  prefix: string,
  marker: string,
  intervalMs: number,
) {
  await page.evaluate(
    ({ sessionId, executionId, prefix, marker, intervalMs }) => {
      const root = document.createElement("section");
      root.id = "legacy-poll-benchmark-renderer";
      root.setAttribute("aria-label", "350 ms polling renderer");
      root.style.position = "fixed";
      root.style.left = "-10000px";
      const output = document.createElement("div");
      output.className = "message-text";
      root.append(output);
      document.body.append(root);

      const state = {
        inFlight: false,
        prefixReady: false,
        checkpointAt: 0,
        requests: { snapshot: 0, history: 0, content: 0 },
        polls: 0,
        negativeReadStartedAt: 0,
        promise: undefined as Promise<LegacyPollResult> | undefined,
        checkpoint: (() => 0) as () => number,
      };
      state.checkpoint = () => {
        if (!state.prefixReady || state.inFlight)
          throw new Error("The polling renderer must be idle after rendering its held prefix.");
        state.checkpointAt = performance.now();
        state.requests = { snapshot: 0, history: 0, content: 0 };
        state.polls = 0;
        state.negativeReadStartedAt = 0;
        return state.checkpointAt;
      };
      const target = window as unknown as {
        __urdLegacyPollBenchmark?: typeof state;
      };
      target.__urdLegacyPollBenchmark = state;

      state.promise = (async () => {
        let text = "";
        let cursor:
          | {
              message: {
                executionId: string;
                messageId: string;
                role: string;
                historyRevision: number;
              };
              segmentIndex: number;
              utf8Offset: number;
            }
          | undefined;
        let positiveReadAt = 0;
        const deadline = performance.now() + 20_000;

        async function get<T>(kind: "snapshot" | "history" | "content", path: string) {
          state.requests[kind]++;
          const startedAt = performance.now();
          const response = await fetch(path, {
            headers: { "X-Urd-Client": "browser", "X-Urd-Benchmark-Poll": "350" },
          });
          if (!response.ok)
            throw new Error(`The 350 ms polling renderer received HTTP ${response.status}.`);
          const value = (await response.json()) as T;
          return { value, startedAt, completedAt: performance.now() };
        }

        while (performance.now() < deadline) {
          const pollStartedAt = performance.now();
          state.inFlight = true;
          state.polls++;
          try {
            const snapshot = await get<{ historyRevision: number }>(
              "snapshot",
              `/api/sessions/${sessionId}/snapshot`,
            );
            const history = await get<{
              entries: {
                entry: { executionId: string; kind: string };
                text: {
                  from: NonNullable<typeof cursor>;
                  availableThrough: NonNullable<typeof cursor>;
                } | null;
              }[];
            }>(
              "history",
              `/api/sessions/${sessionId}/history?mode=latest&expectedHistoryRevision=${snapshot.value.historyRevision}`,
            );
            const outputEntry = history.value.entries.find(
              (entry) =>
                entry.entry.executionId === executionId &&
                entry.entry.kind === "text" &&
                entry.text !== null,
            );
            if (!outputEntry?.text) {
              if (!state.prefixReady) state.negativeReadStartedAt = history.startedAt;
            } else {
              const facts = outputEntry.text;
              cursor ??= facts.from;
              const more =
                cursor.segmentIndex < facts.availableThrough.segmentIndex ||
                (cursor.segmentIndex === facts.availableThrough.segmentIndex &&
                  cursor.utf8Offset < facts.availableThrough.utf8Offset);
              if (!more) {
                if (!text.includes(marker)) state.negativeReadStartedAt = history.startedAt;
              } else {
                const query = new URLSearchParams({
                  executionId: cursor.message.executionId,
                  messageId: cursor.message.messageId,
                  role: cursor.message.role,
                  historyRevision: String(cursor.message.historyRevision),
                  segmentIndex: String(cursor.segmentIndex),
                  utf8Offset: String(cursor.utf8Offset),
                });
                const content = await get<{ text: string; next: NonNullable<typeof cursor> }>(
                  "content",
                  `/api/sessions/${sessionId}/content?${query}`,
                );
                text += content.value.text;
                cursor = content.value.next;
                output.textContent = text;
                if (text.includes(prefix) && !state.prefixReady) {
                  await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
                  state.prefixReady = true;
                }
                if (text.includes(marker)) {
                  positiveReadAt = content.completedAt;
                  await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
                  return {
                    renderedAt: performance.now(),
                    positiveReadAt,
                    negativeReadStartedAt: state.negativeReadStartedAt,
                    polls: state.polls,
                    requestCounts: { ...state.requests },
                  };
                }
              }
            }
          } finally {
            state.inFlight = false;
          }
          await new Promise((resolve) =>
            window.setTimeout(resolve, Math.max(0, pollStartedAt + intervalMs - performance.now())),
          );
        }
        throw new Error("The rendered 350 ms polling path did not observe the fixture marker.");
      })();
    },
    { sessionId, executionId, prefix, marker, intervalMs },
  );
}

async function withOfflineBrowser(
  run: (fixture: {
    model: ModelFixture;
    server: ServerProcess;
    page: Page;
    connection: FixtureConnection;
  }) => Promise<void>,
) {
  const model = await startLocalModelServer();
  let server: ServerProcess | undefined;
  let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
  try {
    const runningServer = await ServerProcess.start();
    server = runningServer;
    const connection = await model.install(runningServer.origin);
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1_024, height: 480 } });
    page.setDefaultTimeout(10_000);
    await run({ model, server: runningServer, page, connection });
  } finally {
    try {
      await browser?.close();
    } finally {
      try {
        await server?.stop();
      } finally {
        await model.close();
      }
    }
  }
}

async function startBrowserEventStream(page: Page) {
  const response = await page.evaluate(async () => {
    const controller = new AbortController();
    const response = await fetch("/api/events", {
      headers: { "X-Urd-Client": "browser" },
      signal: controller.signal,
    });
    const stream = response.body?.getReader();
    if (!stream) throw new Error("The browser event stream has no body.");
    const state = { controller, events: [] as BrowserSseEvent[], readIndex: 0 };
    (window as unknown as { __urdEventFixture?: typeof state }).__urdEventFixture = state;
    void (async () => {
      const decoder = new TextDecoder();
      let buffer = "";
      try {
        while (true) {
          const next = await stream.read();
          if (next.done) return;
          buffer += decoder.decode(next.value, { stream: true });
          let boundary = buffer.indexOf("\n\n");
          while (boundary >= 0) {
            const frame = buffer.slice(0, boundary);
            buffer = buffer.slice(boundary + 2);
            let name = "";
            let data = "";
            for (const line of frame.split(/\r?\n/)) {
              if (line.startsWith("event: ")) name = line.slice(7);
              if (line.startsWith("data: ")) data += line.slice(6);
            }
            if (name && data) state.events.push({ name, data: JSON.parse(data) });
            boundary = buffer.indexOf("\n\n");
          }
        }
      } catch {
        // Aborting the fixture stream is the browser-side disconnect signal.
      }
    })();
    return { status: response.status, contentType: response.headers.get("content-type") };
  });
  assert.equal(response.status, 200);
  assert.match(response.contentType ?? "", /^text\/event-stream/);
  return waitForBrowserEvent(page, "connected");
}

async function waitForBrowserEvent(page: Page, name: string): Promise<BrowserSseEvent> {
  await page.waitForFunction((expected) => {
    const state = (
      window as unknown as { __urdEventFixture?: { events: BrowserSseEvent[]; readIndex: number } }
    ).__urdEventFixture;
    return state?.events.some(
      (event, index) => index >= state.readIndex && event.name === expected,
    );
  }, name);
  return page.evaluate((expected) => {
    const state = (
      window as unknown as { __urdEventFixture?: { events: BrowserSseEvent[]; readIndex: number } }
    ).__urdEventFixture;
    if (!state) throw new Error("The browser event fixture is missing.");
    const index = state.events.findIndex(
      (event, eventIndex) => eventIndex >= state.readIndex && event.name === expected,
    );
    if (index < 0) throw new Error(`The ${expected} event was not queued.`);
    state.readIndex = index + 1;
    return state.events[index]!;
  }, name);
}

async function updateBrowserEventStream(
  page: Page,
  streamId: string,
  open: string[],
  watched: string[],
): Promise<number> {
  return page.evaluate(
    async ({ streamId, open, watched }) => {
      const response = await fetch(`/api/events/${streamId}`, {
        method: "POST",
        headers: { "X-Urd-Client": "browser", "Content-Type": "application/json" },
        body: JSON.stringify({ open, watched }),
      });
      return response.status;
    },
    { streamId, open, watched },
  );
}

async function waitForCondition(condition: () => boolean, description: string, timeoutMs = 5_000) {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${description}.`);
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

async function waitForQuietCount(
  count: () => number,
  quietMs: number,
  timeoutMs: number,
  description: string,
) {
  const deadline = Date.now() + timeoutMs;
  let previous = count();
  let unchangedSince = Date.now();
  while (Date.now() - unchangedSince < quietMs) {
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${description}.`);
    await new Promise((resolve) => setTimeout(resolve, 25));
    const current = count();
    if (current !== previous) {
      previous = current;
      unchangedSince = Date.now();
    }
  }
}

async function waitForSnapshot(
  sessions: SessionClient,
  sessionId: string,
  condition: (snapshot: SessionSnapshot) => boolean,
  description: string,
  timeoutMs = 15_000,
) {
  const deadline = Date.now() + timeoutMs;
  const signal = AbortSignal.timeout(timeoutMs);
  while (Date.now() < deadline) {
    const snapshot = await sessions.getSnapshot(sessionId, signal);
    if (condition(snapshot)) return snapshot;
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`Timed out waiting for ${description}.`);
}

void test("storage retry gates discovery and durable draft recovery", async () => {
  await withOfflineBrowser(async ({ model, server, page }) => {
    let sessionLists = 0;
    let eventStreams = 0;
    let sessionCreates = 0;
    let messageSubmissions = 0;
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    page.on("request", (request) => {
      const url = new URL(request.url());
      if (url.pathname === "/api/sessions" && request.method() === "GET" && eventStreams === 0)
        sessionLists++;
      if (url.pathname === "/api/events" && request.method() === "GET") eventStreams++;
      if (url.pathname === "/api/sessions" && request.method() === "POST") sessionCreates++;
      if (url.pathname.endsWith("/message-submissions") && request.method() === "POST")
        messageSubmissions++;
    });
    await page.addInitScript(() => {
      const getItem = Storage.prototype.getItem;
      Storage.prototype.getItem = function (key) {
        if (key === "urd.workspace.v1" && window.name === "") {
          window.name = "workspace-read-retried";
          throw new DOMException("Temporary read failure", "SecurityError");
        }
        return getItem.call(this, key);
      };
    });

    await page.goto(server.origin.href);
    const retryRead = page.getByRole("alert").getByRole("button", { name: "Retry read" });
    await retryRead.waitFor();
    assert.equal(await page.getByRole("button", { name: "New chat" }).isDisabled(), true);
    assert.equal(await page.getByRole("textbox", { name: "Message" }).count(), 0);
    assert.equal(sessionLists, 0);
    assert.equal(sessionCreates, 0);
    assert.equal(messageSubmissions, 0);

    await retryRead.click();
    const message = page.getByRole("textbox", { name: "Message" });
    await message.waitFor({ timeout: 10_000 });
    assert.equal(
      sessionLists,
      1,
      "workspace discovery must list sessions once after storage retry",
    );
    assert.equal(sessionCreates, 1);

    const modelPicker = page.getByRole("button", { name: /^Model:/ });
    await modelPicker.click();
    await page.getByRole("option", { name: "fixture-a" }).click();
    await message.fill("draft survives reload");
    await page.waitForFunction(() =>
      Object.keys(sessionStorage).some((key) => key.startsWith("urd.workspace.v1.draft.")),
    );
    await page.reload();
    await message.waitFor();
    assert.equal(await message.inputValue(), "draft survives reload");

    model.enqueue({ text: "offline fixture reply" });
    const send = page.getByRole("button", { name: "Send" });
    await page.waitForFunction(() => {
      const button = document.querySelector<HTMLButtonElement>('button[aria-label="Send"]');
      return button !== null && !button.disabled;
    });
    await send.click();
    await page.getByText("offline fixture reply").waitFor();
    await page.waitForFunction(
      () => !Object.keys(sessionStorage).some((key) => key.startsWith("urd.workspace.v1.draft.")),
    );
    assert.equal(messageSubmissions, 1);

    await page.evaluate(() => {
      const original = Storage.prototype.setItem;
      let failed = false;
      Storage.prototype.setItem = function (key, value) {
        if (!failed && key.startsWith("urd.workspace.v1.draft.")) {
          failed = true;
          throw new DOMException("Temporary storage failure", "QuotaExceededError");
        }
        original.call(this, key, value);
      };
      Object.defineProperty(window, "restoreSessionStorage", {
        value: () => {
          Storage.prototype.setItem = original;
        },
      });
    });
    await message.fill("draft after storage recovery");
    await page.getByRole("alert").getByRole("button", { name: "Retry save" }).waitFor();
    assert.equal(await send.isDisabled(), true);
    await page.evaluate(() => {
      (window as unknown as { restoreSessionStorage: () => void }).restoreSessionStorage();
    });
    await page.getByRole("button", { name: "Retry save" }).click();
    await page.waitForFunction(() => {
      const button = document.querySelector<HTMLButtonElement>('button[aria-label="Send"]');
      return button !== null && !button.disabled;
    });
    assert.equal(await message.inputValue(), "draft after storage recovery");
    await page.reload();
    await message.waitFor();
    assert.equal(await message.inputValue(), "draft after storage recovery");
    assert.equal(sessionCreates, 1);
    assert.equal(messageSubmissions, 1);
    assert.deepEqual(pageErrors, []);
  });
});

void test("model and reasoning popovers focus options and restore their triggers on Escape", async () => {
  await withOfflineBrowser(async ({ page, server }) => {
    await page.goto(server.origin.href);
    await page.getByRole("textbox", { name: "Message" }).waitFor();

    const modelPicker = page.getByRole("button", { name: /^Model:/ });
    await modelPicker.click();
    await page.getByRole("option", { name: "fixture-a" }).click();
    await modelPicker.click();
    const search = page.getByRole("combobox", { name: "Search models" });
    await search.waitFor();
    assert.equal(await search.evaluate((element) => element === document.activeElement), true);
    await search.press("Escape");
    await page.waitForFunction(
      () => document.activeElement?.getAttribute("aria-label") === "Model: fixture-a",
    );

    const reasoning = page.getByRole("button", { name: "Reasoning effort: Default" });
    await reasoning.click();
    const defaultReasoning = page.getByRole("option", { name: "Default" });
    await defaultReasoning.waitFor();
    assert.equal(
      await defaultReasoning.evaluate((element) => element === document.activeElement),
      true,
    );
    await defaultReasoning.press("Escape");
    await page.waitForFunction(
      () => document.activeElement?.getAttribute("aria-label") === "Reasoning effort: Default",
    );
  });
});

void test("a session discovered on initial load is named from its first-user preview", async () => {
  await withOfflineBrowser(async ({ model, server, page, connection }) => {
    const sessions = new SessionClient(server.origin);
    const sessionId = await sessions.create();
    const preview = "Discovered session preview title";
    model.enqueue({ text: "fixture reply", delayMs: 10, chunkSize: 8 });
    const receipt = await sessions.submitMessage(sessionId, {
      submissionId: randomUUID(),
      expectedHistoryRevision: 0,
      text: preview,
      llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
    });
    assert.equal(receipt.acceptance, "accepted");
    await waitForSnapshot(
      sessions,
      sessionId,
      (snapshot) => snapshot.lastSettlement?.executionId === receipt.executionId,
      "the preloaded session to settle",
    );
    assert.equal((await sessions.getSnapshot(sessionId)).firstUserPreview, preview);

    await page.goto(server.origin.href);
    const tab = page.locator(`#tab-${sessionId}`);
    await tab.waitFor();
    await page.getByRole("tab", { name: preview }).waitFor();
    assert.equal(await tab.innerText(), preview);
  });
});

void test("browser event streams handshake, track sessions, and resynchronize after reconnect", async () => {
  await withOfflineBrowser(async ({ model, server, page, connection }) => {
    await page.goto(server.origin.href);
    await page.getByRole("textbox", { name: "Message" }).waitFor();

    const sessions = new SessionClient(server.origin);
    const connected = await startBrowserEventStream(page);
    const uuid = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
    assert.match(String(connected.data.stream), uuid);
    assert.match(String(connected.data.boot), uuid);
    assert.deepEqual(Object.keys(connected.data).sort(), ["boot", "stream"]);
    const streamId = String(connected.data.stream);
    const sessionId = await sessions.create();
    const created = await waitForBrowserEvent(page, "session-created");
    assert.deepEqual(created.data, { session: sessionId });

    assert.equal(await updateBrowserEventStream(page, streamId, [sessionId], [sessionId]), 204);
    const registered = await waitForBrowserEvent(page, "status");
    assert.equal(registered.data.session, sessionId);
    assert.equal(registered.data.status, "idle");
    assert.equal(registered.data.boot, connected.data.boot);
    assert.deepEqual(Object.keys(registered.data).sort(), ["boot", "session", "status", "version"]);
    const initialChange = await waitForBrowserEvent(page, "change");
    assert.equal(initialChange.data.session, sessionId);
    assert.equal(initialChange.data.version, registered.data.version);
    assert.deepEqual(Object.keys(initialChange.data).sort(), ["boot", "session", "version"]);

    model.enqueue({ text: "event-stream fixture reply", delayMs: 15, chunkSize: 8 });
    const receipt = await sessions.submitMessage(sessionId, {
      submissionId: randomUUID(),
      expectedHistoryRevision: 0,
      text: "event-stream fixture prompt",
      llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
    });
    assert.equal(receipt.acceptance, "accepted");
    assert.ok(receipt.executionId);
    const running = await waitForBrowserEvent(page, "status");
    assert.equal(running.data.status, "running");
    const contentChange = await waitForBrowserEvent(page, "change");
    assert.equal(contentChange.data.session, sessionId);
    await waitForSnapshot(
      sessions,
      sessionId,
      (snapshot) => snapshot.lastSettlement?.executionId === receipt.executionId,
      "the event-stream execution to settle",
    );
    const settled = await waitForBrowserEvent(page, "status");
    assert.equal(settled.data.status, "idle");

    await page.evaluate(() => {
      (
        window as unknown as { __urdEventFixture: { controller: AbortController } }
      ).__urdEventFixture.controller.abort();
    });
    const staleDeadline = Date.now() + 2_000;
    let staleStatus = 204;
    while (staleStatus !== 404 && Date.now() < staleDeadline) {
      staleStatus = await updateBrowserEventStream(page, streamId, [sessionId], [sessionId]);
      if (staleStatus !== 404) await new Promise((resolve) => setTimeout(resolve, 25));
    }
    assert.equal(staleStatus, 404);

    const reconnected = await startBrowserEventStream(page);
    assert.equal(reconnected.data.boot, connected.data.boot);
    const nextStreamId = String(reconnected.data.stream);
    assert.notEqual(nextStreamId, streamId);
    assert.equal(await updateBrowserEventStream(page, nextStreamId, [sessionId], []), 204);
    assert.equal((await waitForBrowserEvent(page, "status")).data.status, "idle");

    const current = await sessions.getSnapshot(sessionId);
    model.enqueue({ text: "unwatched result" });
    const secondReceipt = await sessions.submitMessage(sessionId, {
      submissionId: randomUUID(),
      expectedHistoryRevision: current.historyRevision,
      text: "unwatched prompt",
      llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
    });
    assert.equal(secondReceipt.acceptance, "accepted");
    assert.equal((await waitForBrowserEvent(page, "status")).data.status, "running");
    await waitForSnapshot(
      sessions,
      sessionId,
      (snapshot) => snapshot.lastSettlement?.executionId === secondReceipt.executionId,
      "the unwatched execution to settle",
    );
    assert.equal((await waitForBrowserEvent(page, "status")).data.status, "idle");
    await page.waitForTimeout(150);
    const unseenChanges = await page.evaluate(() => {
      const state = (
        window as unknown as { __urdEventFixture: { events: BrowserSseEvent[]; readIndex: number } }
      ).__urdEventFixture;
      return state.events.slice(state.readIndex).some((event) => event.name === "change");
    });
    assert.equal(unseenChanges, false);

    assert.equal(await updateBrowserEventStream(page, nextStreamId, [sessionId], [sessionId]), 204);
    assert.equal((await waitForBrowserEvent(page, "status")).data.status, "idle");
    assert.equal((await waitForBrowserEvent(page, "change")).data.session, sessionId);
    await page.evaluate(() => {
      (
        window as unknown as { __urdEventFixture: { controller: AbortController } }
      ).__urdEventFixture.controller.abort();
    });
  });
});

void test("excess tabs pause explicitly while the active tab keeps live updates", async () => {
  await withOfflineBrowser(async ({ model, server, page, connection }) => {
    const sessions = new SessionClient(server.origin);
    const ids: string[] = [];
    for (let index = 0; index < 128; index++) ids.push(await sessions.create());
    const first = ids[0]!;
    const saved = encodeWorkspace({
      version: 1,
      tabs: ids,
      activeId: first,
      chats: Object.fromEntries(
        ids.map((id) => [id, { ...newChat(null), draft: "retained draft" }]),
      ),
      lastSelection: null,
    });
    await page.addInitScript((value) => sessionStorage.setItem("urd.workspace.v1", value), saved);
    let streams = 0;
    const registrations: number[] = [];
    page.on("response", (response) => {
      const path = new URL(response.url()).pathname;
      if (path === "/api/events") streams++;
      if (path.startsWith("/api/events/")) registrations.push(response.status());
    });
    await page.goto(server.origin.href);
    await waitForCondition(() => registrations.includes(204), "initial tab registration");
    const overflow = await sessions.create();
    const warning = page.getByRole("alert").filter({ hasText: "Live updates cover 128 tabs" });
    await warning.waitFor();
    const overflowTab = page.locator(`#tab-${overflow}`);
    await overflowTab.locator('[aria-label="Updates paused"]').waitFor();
    assert.equal(await page.getByRole("tab").count(), 129);

    model.enqueue({ text: "output produced while updates were paused" });
    const receipt = await sessions.submitMessage(overflow, {
      submissionId: randomUUID(),
      expectedHistoryRevision: 0,
      text: "overflow prompt",
      llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
    });
    await waitForSnapshot(
      sessions,
      overflow,
      (snapshot) => snapshot.lastSettlement?.executionId === receipt.executionId,
      "paused session settlement",
    );
    await overflowTab.click();
    await page
      .locator(".message-text")
      .getByText("output produced while updates were paused", { exact: true })
      .waitFor();
    await overflowTab.locator('[aria-label="Updates paused"]').waitFor({ state: "detached" });
    const paused = ids[127]!;
    await page.locator(`#tab-${paused} [aria-label="Updates paused"]`).waitFor();

    model.enqueue({ text: "live output after selecting the overflow tab" });
    await sessions.submitMessage(overflow, {
      submissionId: randomUUID(),
      expectedHistoryRevision: 0,
      text: "now watched",
      llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
    });
    await page
      .locator(".message-text")
      .getByText("live output after selecting the overflow tab", { exact: true })
      .waitFor();
    await page
      .locator(`[data-tab-id="${paused}"]`)
      .getByRole("button", { name: "Close New chat", exact: true })
      .click();
    await warning.waitFor({ state: "detached" });
    assert.equal(await page.getByRole("tab").count(), 128);
    assert.equal(await page.locator('[aria-label="Updates paused"]').count(), 0);
    await page.locator(`#tab-${first}`).click();
    assert.equal(
      await page.getByRole("textbox", { name: "Message" }).inputValue(),
      "retained draft",
    );
    assert.equal(streams, 1, "changing overflow selection must not reconnect");
    assert.ok(registrations.length > 1 && registrations.every((status) => status === 204));
  });
});

void test(
  "rendered push discovers sessions, cleans up hidden streams, reconnects status, and measures against rendered 350 ms polling",
  { timeout: 60_000 },
  async () => {
    await withOfflineBrowser(async ({ model, server, page, connection }) => {
      await installBrowserPushProbe(page);
      const pollReadCounts = new Map<string, number>();
      const pageErrors: string[] = [];
      let finishInitialDiscovery!: () => void;
      let initialDiscoveryResponses = 0;
      const initialDiscoveryFinished = new Promise<void>((resolve) => {
        finishInitialDiscovery = resolve;
      });
      page.on("pageerror", (error) => pageErrors.push(error.message));
      page.on("response", (response) => {
        const url = new URL(response.url());
        if (url.pathname === "/api/sessions" && response.request().method() === "GET")
          void response.finished().then(() => {
            initialDiscoveryResponses++;
            if (initialDiscoveryResponses === 2) finishInitialDiscovery();
          });
      });
      page.on("request", (request) => {
        const url = new URL(request.url());
        const match = url.pathname.match(/^\/api\/sessions\/([^/]+)\/(snapshot|history|content)$/);
        if (!match || request.method() !== "GET") return;
        const sessionId = match[1]!;
        if (request.headers()["x-urd-benchmark-poll"] === "350")
          pollReadCounts.set(sessionId, (pollReadCounts.get(sessionId) ?? 0) + 1);
      });

      await page.goto(server.origin.href);
      await page.getByRole("textbox", { name: "Message" }).waitFor();
      await page.waitForFunction(() => {
        const probe = (window as unknown as { __urdPushProbe?: { streams: unknown[] } })
          .__urdPushProbe;
        return (probe?.streams.length ?? 0) > 0;
      });
      await initialDiscoveryFinished;
      await page.evaluate(
        () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
      );

      const sessions = new SessionClient(server.origin);
      const sessionId = await sessions.create();
      const tab = page.locator(`#tab-${sessionId}`);
      await tab.waitFor();
      await page.waitForFunction((id) => {
        const probe = (window as unknown as { __urdPushProbe?: { events: BrowserPushEvent[] } })
          .__urdPushProbe;
        return probe?.events.some(
          (event) => event.name === "session-created" && event.data.session === id,
        );
      }, sessionId);
      assert.equal(await page.getByRole("tab").count(), 2);

      await tab.click();
      await page.waitForFunction((id) => location.hash.slice(1) === id, sessionId);
      await page.waitForFunction((id) => {
        const probe = (window as unknown as { __urdPushProbe?: { events: BrowserPushEvent[] } })
          .__urdPushProbe;
        return probe?.events.some(
          (event) =>
            event.name === "status" && event.data.session === id && event.data.status === "idle",
        );
      }, sessionId);
      await page.mouse.move(0, 0);
      await tab.hover();
      await page.getByRole("tooltip").getByText("Idle", { exact: true }).waitFor();
      await page.getByRole("button", { name: /^Model:/ }).click();
      await page.getByRole("option", { name: "fixture-a" }).click();

      const longReply = "reconnected push stream remains visibly running";
      model.enqueue({ text: longReply, delayMs: 100, chunkSize: 1 });
      const initial = await sessions.getSnapshot(sessionId);
      const receipt = await sessions.submitMessage(sessionId, {
        submissionId: randomUUID(),
        expectedHistoryRevision: initial.historyRevision,
        text: "render push stream state",
        llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
      });
      assert.equal(receipt.acceptance, "accepted");
      const running = page.locator(`#tab-${sessionId} [aria-label="Running"]`);
      await running.waitFor();

      const hiddenStreamId = await page.evaluate(() => {
        const probe = (
          window as unknown as {
            __urdPushProbe: { streams: { id: string | null; signal: AbortSignal | null }[] };
          }
        ).__urdPushProbe;
        const stream = probe.streams.at(-1);
        if (!stream?.id) throw new Error("The connected stream identity was not observed.");
        Object.defineProperty(document, "hidden", { configurable: true, value: true });
        document.dispatchEvent(new Event("visibilitychange"));
        return stream.id;
      });
      await page.waitForFunction(() => {
        const probe = (
          window as unknown as {
            __urdPushProbe?: { streams: { signal: AbortSignal | null }[] };
          }
        ).__urdPushProbe;
        return probe?.streams.at(-1)?.signal?.aborted === true;
      });
      await running.waitFor({ state: "detached" });
      const cleanupDeadline = Date.now() + 2_000;
      let staleStreamStatus = 204;
      while (staleStreamStatus !== 404 && Date.now() < cleanupDeadline) {
        staleStreamStatus = await updateBrowserEventStream(
          page,
          hiddenStreamId,
          [sessionId],
          [sessionId],
        );
        if (staleStreamStatus !== 404) await new Promise((resolve) => setTimeout(resolve, 25));
      }
      assert.equal(staleStreamStatus, 404, "hiding the page must release its server registration");

      await page.evaluate(() => {
        Object.defineProperty(document, "hidden", { configurable: true, value: false });
        document.dispatchEvent(new Event("visibilitychange"));
      });
      await page.waitForFunction(() => {
        const probe = (
          window as unknown as { __urdPushProbe?: { streams: { id: string | null }[] } }
        ).__urdPushProbe;
        return (probe?.streams.length ?? 0) >= 2 && Boolean(probe?.streams.at(-1)?.id);
      });
      const reconnectedStreamId = await page.evaluate(() => {
        const probe = (
          window as unknown as {
            __urdPushProbe: { streams: { id: string | null }[] };
          }
        ).__urdPushProbe;
        return probe.streams.at(-1)?.id;
      });
      assert.ok(reconnectedStreamId);
      assert.notEqual(reconnectedStreamId, hiddenStreamId);
      await running.waitFor();

      await page.getByText(longReply, { exact: true }).waitFor();
      await waitForSnapshot(
        sessions,
        sessionId,
        (snapshot) => snapshot.lastSettlement?.executionId === receipt.executionId,
        "the reconnect acceptance execution to settle",
      );
      await page.waitForFunction(
        (id) => !document.querySelector(`#tab-${id} [aria-label="Running"]`),
        sessionId,
      );

      const benchmarkResults: {
        pushCommitToRenderMs: { lower: number; upper: number };
        pollingCommitToRenderMs: { lower: number; upper: number };
        pollMs: number;
        pollCycles: number;
        requestCounts: {
          push: { snapshot: number; history: number; content: number };
          polling: { snapshot: number; history: number; content: number };
        };
        pushChangeNotices: number;
      }[] = [];
      for (let sample = 0; sample < 2; sample++) {
        const prefix = `held-output-prefix-${sample}-${"p".repeat(80)}-`;
        const marker = `poll-push-marker-${sample}-${randomUUID()}`;
        model.enqueue({ text: `${prefix}${marker}`, chunkSize: prefix.length, delayMs: 1_000 });
        const beforeBenchmark = await sessions.getSnapshot(sessionId);
        const benchmarkReceipt = await sessions.submitMessage(sessionId, {
          submissionId: randomUUID(),
          expectedHistoryRevision: beforeBenchmark.historyRevision,
          text: `measure push delivery ${sample}`,
          llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
        });
        assert.equal(benchmarkReceipt.acceptance, "accepted");
        assert.ok(benchmarkReceipt.executionId);
        await startLegacyPollingRenderer(
          page,
          sessionId,
          benchmarkReceipt.executionId,
          prefix,
          marker,
          350,
        );
        await page.waitForFunction(() => {
          const state = (
            window as unknown as {
              __urdLegacyPollBenchmark?: { prefixReady: boolean; inFlight: boolean };
            }
          ).__urdLegacyPollBenchmark;
          return state?.prefixReady && !state.inFlight;
        });
        await page.waitForFunction(
          (value) =>
            [...document.querySelectorAll("#chat-view .transcript .message-text")].some((node) =>
              node.textContent?.includes(value),
            ),
          prefix,
        );
        const checkpointAt = await page.evaluate(() => {
          const state = (
            window as unknown as {
              __urdLegacyPollBenchmark?: { checkpoint(): number };
            }
          ).__urdLegacyPollBenchmark;
          if (!state) throw new Error("The polling renderer checkpoint is missing.");
          return state.checkpoint();
        });
        const beforePollReads = pollReadCounts.get(sessionId) ?? 0;
        const pushRenderedPromise = waitForRenderedMarker(page, marker);
        const pollingRenderer = page.locator("#legacy-poll-benchmark-renderer .message-text");
        const [pushRenderedAt] = await Promise.all([
          pushRenderedPromise,
          page
            .locator("#chat-view .transcript .message-text")
            .filter({ hasText: marker })
            .waitFor(),
          pollingRenderer.filter({ hasText: marker }).waitFor(),
        ]);
        const poll = await page.evaluate(async () => {
          const state = window as unknown as {
            __urdLegacyPollBenchmark?: { promise?: Promise<LegacyPollResult> };
          };
          if (!state.__urdLegacyPollBenchmark?.promise)
            throw new Error("The polling renderer result is missing.");
          return state.__urdLegacyPollBenchmark.promise;
        });
        const rendered = await page.evaluate(
          ({ id, checkpointAt, pushRenderedAt, poll }) => {
            const probe = (
              window as unknown as {
                __urdPushProbe: { events: BrowserPushEvent[]; reads: BenchmarkRead[] };
              }
            ).__urdPushProbe;
            const changes = probe.events.filter(
              (event) =>
                event.name === "change" &&
                event.data.session === id &&
                event.at >= checkpointAt &&
                event.at <= pushRenderedAt,
            );
            const notification = changes.at(-1);
            if (!notification)
              throw new Error("The output change notice was not observed before push rendering.");
            const readsThrough = (at: number, polling: boolean) => {
              const paths = { snapshot: 0, history: 0, content: 0 };
              for (const read of probe.reads) {
                if (read.at < checkpointAt || read.at > at || read.polling !== polling) continue;
                paths[read.path as keyof typeof paths]++;
              }
              return paths;
            };
            return {
              pushNoticeAt: notification.at,
              pushReads: readsThrough(pushRenderedAt, false),
              pollingReads: readsThrough(poll.renderedAt, true),
              pushCommitToRenderMs: {
                lower: pushRenderedAt - notification.at,
                upper: pushRenderedAt - poll.negativeReadStartedAt,
              },
              pollingCommitToRenderMs: {
                lower: poll.renderedAt - poll.positiveReadAt,
                upper: poll.renderedAt - poll.negativeReadStartedAt,
              },
              pushChangeNotices: changes.length,
            };
          },
          { id: sessionId, checkpointAt, pushRenderedAt, poll },
        );
        const pollReads = (pollReadCounts.get(sessionId) ?? 0) - beforePollReads;
        assert.ok(poll.negativeReadStartedAt >= checkpointAt);
        assert.ok(poll.positiveReadAt >= poll.negativeReadStartedAt);
        assert.ok(rendered.pushCommitToRenderMs.upper >= rendered.pushCommitToRenderMs.lower);
        assert.ok(rendered.pollingCommitToRenderMs.upper >= rendered.pollingCommitToRenderMs.lower);
        assert.equal(
          pollReads,
          poll.requestCounts.snapshot + poll.requestCounts.history + poll.requestCounts.content,
          "the 350 ms renderer's request counter must match browser HTTP requests",
        );
        assert.deepEqual(rendered.pollingReads, poll.requestCounts);
        assert.ok(
          rendered.pushReads.snapshot + rendered.pushReads.history + rendered.pushReads.content > 0,
        );
        assert.ok(poll.polls >= 2, "the held output must include a negative polling observation");
        benchmarkResults.push({
          pushCommitToRenderMs: rendered.pushCommitToRenderMs,
          pollingCommitToRenderMs: rendered.pollingCommitToRenderMs,
          pollMs: 350,
          pollCycles: poll.polls,
          requestCounts: { push: rendered.pushReads, polling: rendered.pollingReads },
          pushChangeNotices: rendered.pushChangeNotices,
        });
        await page.evaluate(() => {
          document.querySelector("#legacy-poll-benchmark-renderer")?.remove();
          delete (window as unknown as { __urdLegacyPollBenchmark?: unknown })
            .__urdLegacyPollBenchmark;
        });
        await waitForSnapshot(
          sessions,
          sessionId,
          (snapshot) => snapshot.lastSettlement?.executionId === benchmarkReceipt.executionId,
          `benchmark execution ${sample} to settle`,
        );
      }
      assert.deepEqual(pageErrors, []);
      console.log(
        JSON.stringify({
          benchmark: "same local execution rendered by push UI and a 350 ms polling renderer",
          fixture: "each reply exposes a prefix, then withholds a unique marker for 1,000 ms",
          pollingPath:
            "test-side renderer polls current snapshot/history/content routes every 350 ms and paints the returned text; not the historical client binary",
          commitTiming:
            "no transaction timestamp used; lower/upper latency bounds use the last negative history-read start and each path's first post-commit observation",
          samples: benchmarkResults,
        }),
      );
    });
  },
);

void test("deep links, tab order and close focus preserve hidden drafts across reload", async () => {
  await withOfflineBrowser(async ({ server, page }) => {
    const sessions = new SessionClient(server.origin);
    const firstId = await sessions.create();
    const secondId = await sessions.create();
    let creates = 0;
    page.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/sessions" && request.method() === "POST")
        creates++;
    });
    await page.goto(`${server.origin.href}#${secondId}`);
    const message = page.getByRole("textbox", { name: "Message" });
    await message.waitFor();
    assert.equal(await page.getByRole("tab").count(), 1);
    assert.equal(await page.getByRole("tab").getAttribute("id"), `tab-${secondId}`);
    await message.fill("retained while hidden");

    await page.evaluate((id) => {
      location.hash = id;
    }, firstId);
    await page.locator(`#tab-${firstId}[aria-selected=true]`).waitFor();
    await page.locator(`#tab-${secondId}`).click();
    await page.locator(`#tab-${secondId}`).press("Alt+Shift+ArrowRight");
    await page.waitForFunction(
      (id) => document.querySelector('[role="tab"]')?.id === `tab-${id}`,
      firstId,
    );
    assert.equal(await message.inputValue(), "retained while hidden");
    await page.locator(`#tab-${secondId}`).press("Delete");
    await page.waitForFunction((id) => document.activeElement?.id === `tab-${id}`, firstId);
    await page.locator(`#tab-${firstId}`).press("Delete");
    await page.waitForFunction(
      () => document.activeElement?.getAttribute("aria-label") === "New chat",
    );
    await page.waitForFunction(() => location.hash === "");

    // An intentionally empty saved workspace must not trigger another automatic Create.
    await page.reload();
    await page.waitForFunction(() => {
      const button = document.querySelector<HTMLButtonElement>('button[aria-label="New chat"]');
      return button !== null && !button.disabled;
    });
    assert.equal(await page.getByRole("tab").count(), 0);
    await page.evaluate((id) => {
      location.hash = id;
    }, secondId);
    await message.waitFor();
    assert.equal(await message.inputValue(), "retained while hidden");
    assert.equal(creates, 0);
  });
});

void test("workspace read recovery reconciles a hidden uncertain send", async () => {
  await withOfflineBrowser(async ({ model, server, page }) => {
    let submissions = 0;
    const receiptReads: string[] = [];
    page.on("request", (request) => {
      const { pathname } = new URL(request.url());
      if (request.method() === "POST" && pathname.endsWith("/message-submissions")) submissions++;
      else if (
        request.method() === "GET" &&
        /^\/api\/sessions\/[^/]+\/message-submissions\/[^/]+$/.test(pathname)
      )
        receiptReads.push(pathname);
    });
    await page.addInitScript(() => {
      const getItem = Storage.prototype.getItem;
      Storage.prototype.getItem = function (key) {
        if (key === "urd.workspace.v1" && window.name === "fail-next-workspace-read") {
          window.name = "workspace-read-failed";
          throw new DOMException("Temporary read failure", "SecurityError");
        }
        return getItem.call(this, key);
      };
    });
    await page.route("**/api/sessions/*/message-submissions", async (route) => {
      if (route.request().method() !== "POST") return route.continue();
      await route.fetch();
      await route.abort("failed");
    });

    await page.goto(server.origin.href);
    const message = page.getByRole("textbox", { name: "Message" });
    await page.getByRole("button", { name: /^Model:/ }).click();
    await page.getByRole("option", { name: "fixture-a" }).click();
    const originalId = new URL(page.url()).hash.slice(1);
    model.enqueue({ text: "accepted fixture reply" });
    await message.fill("accepted despite lost acknowledgement");
    await page.getByRole("button", { name: "Send" }).click();
    await page.getByText("Send uncertain.", { exact: false }).waitFor();

    await page.getByRole("button", { name: "New chat", exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('[role="tab"]').length === 2);
    await page.waitForFunction((id) => location.hash.slice(1) !== id, originalId);
    const secondId = new URL(page.url()).hash.slice(1);
    await message.fill("second chat draft");
    await page.waitForFunction(() =>
      Object.keys(sessionStorage).some((key) => key.startsWith("urd.workspace.v1.draft.")),
    );
    await page.evaluate(() => {
      window.name = "fail-next-workspace-read";
    });
    await page.reload();
    const retryRead = page.getByRole("alert").getByRole("button", { name: "Retry read" });
    await retryRead.waitFor();
    await waitForQuietCount(() => receiptReads.length, 300, 2_000, "receipt reads to remain gated");
    assert.equal(receiptReads.length, 0);

    await retryRead.click();
    await page.waitForFunction(
      ({ originalId, secondId }) => {
        const saved = JSON.parse(sessionStorage.getItem("urd.workspace.v1") ?? "null");
        return (
          saved?.activeId === secondId &&
          saved.chats[originalId]?.pending === null &&
          saved.chats[originalId]?.draft === ""
        );
      },
      { originalId, secondId },
    );
    await waitForQuietCount(() => receiptReads.length, 300, 2_000, "receipt reads to settle");
    assert.equal(receiptReads.length, 1);
    assert.match(receiptReads[0]!, new RegExp(`^/api/sessions/${originalId}/message-submissions/`));
    assert.equal(await message.inputValue(), "second chat draft");

    await page.locator(`#tab-${originalId}`).click();
    await page.getByText("accepted despite lost acknowledgement", { exact: false }).waitFor();
    assert.equal(submissions, 1);
  });
});

void test("an unrelated successful Send cannot mark a refused resend accepted", async () => {
  await withOfflineBrowser(async ({ model, server, page }) => {
    const sourceSubmissionId = "30000000-0000-0000-0000-000000000003";
    const sourceExecutionId = "20000000-0000-0000-0000-000000000002";
    const sessions = new SessionClient(server.origin);
    await page.goto(server.origin.href);
    const message = page.getByRole("textbox", { name: "Message" });
    await message.waitFor();
    const sessionId = new URL(page.url()).hash.slice(1);
    const snapshot = await sessions.getSnapshot(sessionId);
    const source: UserMessageSubmission = {
      submissionId: sourceSubmissionId,
      expectedHistoryRevision: snapshot.historyRevision,
      text: "Original not-sent input",
      llmSelection: null,
    };
    const sourceReceipt: UserMessageReceipt = {
      sessionId,
      submission: source,
      acceptance: "accepted",
      refusalReason: null,
      executionId: sourceExecutionId,
    };
    let submissions = 0;
    await page.route("**/api/sessions/*/not-sent*", (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          inputs: [
            {
              submissionId: sourceSubmissionId,
              executionId: sourceExecutionId,
              preview: "Original preview",
            },
          ],
          nextCursor: null,
        }),
      }),
    );
    await page.route("**/api/sessions/*/message-submissions/*", (route) => {
      if (route.request().method() !== "GET") return route.continue();
      const submissionId = new URL(route.request().url()).pathname.split("/").at(-1);
      if (submissionId === sourceSubmissionId)
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify(sourceReceipt),
        });
      return route.fulfill({ status: 404 });
    });
    await page.route("**/api/sessions/*/message-submissions", async (route) => {
      if (route.request().method() !== "POST") return route.continue();
      submissions++;
      if (submissions === 1) {
        const submission = route.request().postDataJSON() as UserMessageSubmission;
        return route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            sessionId,
            submission,
            acceptance: "refused",
            refusalReason: "staleRevision",
            executionId: null,
          } satisfies UserMessageReceipt),
        });
      }
      return route.continue();
    });

    await page.getByRole("button", { name: "Not sent" }).click();
    const input = page.locator("li").filter({ hasText: "Original preview" });
    await input.getByRole("button", { name: "Retrieve" }).click();
    await input.getByText("Original not-sent input", { exact: true }).waitFor();
    await input.getByRole("button", { name: "Resend" }).click();
    await page.getByText("Not resent: staleRevision.", { exact: true }).waitFor();
    assert.equal(await page.getByText("Resend accepted.", { exact: true }).count(), 0);
    assert.equal(await input.getByRole("button", { name: "Resend" }).isEnabled(), true);

    await page.getByRole("button", { name: /^Model:/ }).click();
    await page.getByRole("option", { name: "fixture-a" }).click();
    model.enqueue({ text: "unrelated successful reply" });
    await message.fill("Unrelated successful Send");
    await page.getByRole("button", { name: "Send", exact: true }).click();
    await page.getByText("unrelated successful reply", { exact: true }).waitFor();
    await page.getByText("Not resent: staleRevision.", { exact: true }).waitFor();
    assert.equal(await page.getByText("Resend accepted.", { exact: true }).count(), 0);
    assert.equal(await input.getByRole("button", { name: "Resend" }).isEnabled(), true);
    assert.equal(submissions, 2);
  });
});

void test("not-sent discovery is user-invoked, retrieves on demand, and resends without replacing the draft", async () => {
  await withOfflineBrowser(async ({ server, page }) => {
    const pageCursor = "next-cursor";
    const firstSubmissionId = "30000000-0000-0000-0000-000000000003";
    const firstExecutionId = "20000000-0000-0000-0000-000000000002";
    const resendExecutionId = "20000000-0000-0000-0000-000000000004";
    const sourceText = "Full original text, shown only after retrieval.";
    const sessions = new SessionClient(server.origin);
    const reads: (string | null)[] = [];
    const resent: UserMessageSubmission[] = [];
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    await page.goto(server.origin.href);
    const message = page.getByRole("textbox", { name: "Message" });
    await message.waitFor();
    const sessionId = new URL(page.url()).hash.slice(1);
    const snapshot = await sessions.getSnapshot(sessionId);
    const original: UserMessageSubmission = {
      submissionId: firstSubmissionId,
      expectedHistoryRevision: snapshot.historyRevision,
      text: sourceText,
      llmSelection: null,
    };
    const originalReceipt: UserMessageReceipt = {
      sessionId,
      submission: original,
      acceptance: "accepted",
      refusalReason: null,
      executionId: firstExecutionId,
    };

    await page.route("**/api/sessions/*/not-sent*", async (route) => {
      const url = new URL(route.request().url());
      reads.push(url.searchParams.get("cursor"));
      const result =
        url.searchParams.get("cursor") === pageCursor
          ? {
              inputs: [
                {
                  submissionId: "30000000-0000-0000-0000-000000000005",
                  executionId: "20000000-0000-0000-0000-000000000006",
                  preview: "Second page preview",
                },
              ],
              nextCursor: null,
            }
          : {
              inputs: [
                {
                  submissionId: firstSubmissionId,
                  executionId: firstExecutionId,
                  preview: "Preview only",
                },
              ],
              nextCursor: pageCursor,
            };
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(result),
      });
    });
    await page.route("**/api/sessions/*/message-submissions/*", async (route) => {
      if (route.request().method() !== "GET") return route.continue();
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(originalReceipt),
      });
    });
    await page.route("**/api/sessions/*/message-submissions", async (route) => {
      if (route.request().method() !== "POST") return route.continue();
      const submission = route.request().postDataJSON() as UserMessageSubmission;
      resent.push(submission);
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          sessionId,
          submission,
          acceptance: "accepted",
          refusalReason: null,
          executionId: resendExecutionId,
        } satisfies UserMessageReceipt),
      });
    });

    await message.fill("Keep this unrelated draft.");
    await waitForQuietCount(() => reads.length, 250, 2_000, "not-sent discovery to remain idle");
    assert.equal(reads.length, 0, "not-sent discovery must wait for an explicit request");
    await page.getByRole("button", { name: "Not sent" }).click();
    await page.getByText("Preview only", { exact: true }).waitFor();
    assert.equal(await page.getByText(sourceText, { exact: true }).count(), 0);
    assert.deepEqual(reads, [null]);

    const firstInput = page.locator("li").filter({ hasText: "Preview only" });
    await firstInput.getByRole("button", { name: "Retrieve" }).click();
    await page.locator("pre").getByText(sourceText, { exact: true }).waitFor();
    assert.equal(await message.inputValue(), "Keep this unrelated draft.");
    await firstInput.getByRole("button", { name: "Resend" }).click();
    await page.getByText("Resend accepted.", { exact: true }).waitFor();
    assert.equal(await firstInput.getByRole("button", { name: "Accepted" }).isDisabled(), true);
    assert.equal(resent.length, 1);
    assert.notEqual(resent[0]?.submissionId, firstSubmissionId);
    assert.equal(resent[0]?.expectedHistoryRevision, snapshot.historyRevision);
    assert.equal(resent[0]?.text, sourceText);
    assert.equal(await message.inputValue(), "Keep this unrelated draft.");

    await page.getByRole("button", { name: "Next page" }).click();
    await waitForCondition(() => reads.length >= 2, "the next not-sent page read");
    assert.deepEqual(reads, [null, pageCursor]);
    await page.getByText("Second page preview", { exact: true }).waitFor();
    await page.getByRole("button", { name: "Previous page" }).click();
    await page.getByText("Preview only", { exact: true }).waitFor();
    assert.equal(reads.at(-1), null);

    await page.reload();
    await message.waitFor();
    await waitForQuietCount(() => reads.length, 250, 2_000, "reload to remain on demand");
    assert.equal(reads.length, 3);
    await page.getByRole("button", { name: "Not sent" }).click();
    await page.getByText("Preview only", { exact: true }).waitFor();
    assert.equal(reads.length, 4, "a later browser run can discover not-sent inputs again");
    const reloadedInput = page.locator("li").filter({ hasText: "Preview only" });
    await reloadedInput.getByRole("button", { name: "Retrieve" }).click();
    await page.locator("pre").getByText(sourceText, { exact: true }).waitFor();
    assert.equal(resent.length, 1, "reload must not automatically resend the source");
    await reloadedInput.getByRole("button", { name: "Resend" }).click();
    await page.getByText("Resend accepted.", { exact: true }).waitFor();
    assert.equal(resent.length, 2);
    assert.notEqual(resent[1]?.submissionId, resent[0]?.submissionId);
    assert.notEqual(resent[1]?.submissionId, firstSubmissionId);
    assert.equal(await message.inputValue(), "Keep this unrelated draft.");
    assert.deepEqual(pageErrors, []);
  });
});

void test(
  "pending input promotion appends history without moving the reading anchor",
  { timeout: 45_000 },
  async () => {
    await withOfflineBrowser(async ({ model, server, page }) => {
      const pageErrors: string[] = [];
      page.on("pageerror", (error) => pageErrors.push(error.message));
      await page.goto(server.origin.href);
      const message = page.getByRole("textbox", { name: "Message" });
      await message.waitFor();
      await page.getByRole("button", { name: /^Model:/ }).click();
      await page.getByRole("option", { name: "fixture-a" }).click();
      const send = page.getByRole("button", { name: "Send" });
      const sessions = new SessionClient(server.origin);

      model.enqueue({
        text: `bootstrap-anchor\n${Array.from(
          { length: 18 },
          (_, index) => `history line ${index} ${"context ".repeat(4)}`,
        ).join("\n")}`,
      });
      await message.fill("initialize promotion transcript");
      await send.click();
      await page.getByText("history line 17", { exact: false }).waitFor();

      const sessionId = new URL(page.url()).hash.slice(1);
      model.enqueue({ text: `first long reply ${"x".repeat(500)}`, chunkSize: 1, delayMs: 5 });
      await message.fill("first execution before queued input");
      await send.click();
      await page.getByRole("button", { name: "Stop" }).waitFor();
      const firstRunning = await waitForSnapshot(
        sessions,
        sessionId,
        (snapshot) => snapshot.unsettledExecutionId !== null,
        "the first execution to run",
      );
      const firstExecutionId = firstRunning.unsettledExecutionId;
      assert.ok(firstExecutionId);
      await waitForCondition(() => model.calls.length >= 2, "the first provider request");
      model.enqueue({ text: "reply after pending input promotion" });

      const pendingText = "queued input is promoted here";
      await message.fill(pendingText);
      await page.waitForFunction(() => {
        const button = document.querySelector<HTMLButtonElement>('button[aria-label="Send"]');
        return button !== null && !button.disabled;
      });
      await send.click();
      const pendingSnapshot = await waitForSnapshot(
        sessions,
        sessionId,
        (snapshot) => snapshot.pendingInputs.some((input) => input.preview === pendingText),
        "the queued input to appear separately from history",
      );
      assert.equal(pendingSnapshot.pendingInputs.length, 1);
      const pendingSubmissionId = pendingSnapshot.pendingInputs[0]!.submissionId;
      const pendingSummary = page.getByText("Pending (1)", { exact: true });
      await pendingSummary.waitFor();
      await pendingSummary.click();
      await page.locator("details").getByText(pendingText, { exact: true }).waitFor();
      const conversation = page.getByRole("region", { name: "Conversation" });
      assert.equal(await conversation.getByText(pendingText, { exact: true }).count(), 0);

      const anchor = page.getByText("bootstrap-anchor", { exact: false });
      const anchorBox = await anchor.boundingBox();
      const conversationBox = await conversation.boundingBox();
      assert.ok(anchorBox);
      assert.ok(conversationBox);
      await conversation.evaluate((node, top) => {
        node.scrollTop = Math.max(0, top - node.getBoundingClientRect().top - 12);
      }, anchorBox.y);
      await page.getByRole("button", { name: "Jump to latest" }).waitFor();
      await conversation.evaluate(
        () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
      );
      const anchorBefore = await anchor.boundingBox();
      assert.ok(anchorBefore);

      const promotedSnapshot = await waitForSnapshot(
        sessions,
        sessionId,
        (snapshot) =>
          (snapshot.unsettledExecutionId === firstExecutionId ||
            snapshot.lastSettlement?.executionId === firstExecutionId) &&
          snapshot.pendingInputs.length === 0,
        "the queued input to promote into the current execution",
      );
      const promotedReceipt = await sessions.getMessageReceipt(sessionId, pendingSubmissionId);
      assert.equal(promotedReceipt?.submission.submissionId, pendingSubmissionId);
      assert.equal(promotedReceipt?.executionId, firstExecutionId);
      const history = await sessions.getHistory(sessionId, {
        mode: "latest",
        expectedHistoryRevision: promotedSnapshot.historyRevision,
      });
      const promotedUser = history.entries.find(
        (entry) =>
          entry.entry.kind === "user" &&
          entry.entry.executionId === promotedReceipt.executionId &&
          entry.entry.entryId === pendingSubmissionId,
      );
      assert.ok(promotedUser?.text, "the pending submission's user input must be in history");
      assert.equal(
        (await sessions.getMessageContent(sessionId, promotedUser.text.from)).text,
        pendingText,
      );
      const promotedKey = `${promotedUser.entry.executionId}/${promotedUser.entry.entryId}/${promotedUser.entry.kind}`;
      await page.waitForFunction(
        (key) =>
          [...document.querySelectorAll<HTMLElement>("#chat-view .transcript [data-message]")].some(
            (item) => item.dataset.message === key,
          ),
        promotedKey,
      );
      await page.getByText("Pending (1)", { exact: true }).waitFor({ state: "detached" });
      const anchorAfter = await anchor.boundingBox();
      assert.ok(anchorAfter);
      assert.ok(Math.abs(anchorAfter.y - anchorBefore.y) <= 2);
      assert.deepEqual(pageErrors, []);
    });
  },
);

void test("delayed follow scrolls survive lazy expansion but user scroll-away keeps its anchor", async () => {
  await withOfflineBrowser(async ({ model, server, page, connection }) => {
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    const sessions = new SessionClient(server.origin);
    const sessionId = await sessions.create();
    const lines = (marker: string) =>
      `${marker}\n${Array.from({ length: 30 }, (_, index) => `${marker} line ${index}`).join("\n")}`;
    for (const [prompt, reply] of [
      [lines("first-lazy-body"), lines("second-lazy-body")],
      ["latest prompt", "latest-lazy-marker"],
    ] as const) {
      model.enqueue({ text: reply });
      const receipt = await sessions.submitMessage(sessionId, {
        submissionId: randomUUID(),
        expectedHistoryRevision: 0,
        text: prompt,
        llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
      });
      await waitForSnapshot(
        sessions,
        sessionId,
        (snapshot) => snapshot.lastSettlement?.executionId === receipt.executionId,
        "the lazy-body fixture to settle",
      );
    }
    const history = await sessions.getHistory(sessionId, {
      mode: "latest",
      expectedHistoryRevision: 0,
    });
    let firstBodyOnly = true;
    // Advertise the other bodies after the first follow, independent of native IO callback order.
    await page.route(`**/api/sessions/${sessionId}/history?*`, async (route) => {
      if (!firstBodyOnly) return route.continue();
      await route.fulfill({
        json: {
          ...history,
          entries: history.entries.map((entry, index) => ({
            ...entry,
            text:
              index > 0 && entry.text
                ? { ...entry.text, availableThrough: entry.text.from }
                : entry.text,
          })),
        },
      });
    });
    await page.addInitScript(() => {
      const state = { held: false, replayed: false, gap: 0 };
      (window as unknown as { __urdDelayedScroll: typeof state }).__urdDelayedScroll = state;
      let held: Event | null = null;
      let target: HTMLElement | null = null;
      // Delay one real follow scroll until the next lazy body commits. IO/RO remain native.
      document.addEventListener(
        "scroll",
        (event) => {
          const node = event.target;
          if (
            state.held ||
            !(node instanceof HTMLElement) ||
            !node.matches(".transcript") ||
            node.scrollTop === 0 ||
            !node.textContent?.includes("first-lazy-body")
          )
            return;
          event.stopImmediatePropagation();
          state.held = true;
          held = event;
          target = node;
        },
        true,
      );
      new MutationObserver(() => {
        if (!held || !target?.textContent?.includes("second-lazy-body")) return;
        state.gap = target.scrollHeight - target.scrollTop - target.clientHeight;
        const event = held;
        held = null;
        target.dispatchEvent(event);
        state.replayed = true;
      }).observe(document, { childList: true, characterData: true, subtree: true });
    });

    await page.goto(`${server.origin.href}#${sessionId}`);
    await page.waitForFunction(
      () =>
        (window as unknown as { __urdDelayedScroll: { held: boolean } }).__urdDelayedScroll.held,
    );
    firstBodyOnly = false;
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await page.waitForFunction(
      () =>
        (window as unknown as { __urdDelayedScroll: { replayed: boolean } }).__urdDelayedScroll
          .replayed,
    );
    const delayedGap = await page.evaluate(
      () => (window as unknown as { __urdDelayedScroll: { gap: number } }).__urdDelayedScroll.gap,
    );
    assert.ok(delayedGap > 40, "the held follow scroll must arrive after another body expands");
    await page.getByText("latest-lazy-marker", { exact: true }).waitFor();
    const conversation = page.getByRole("region", { name: "Conversation" });
    await page.waitForFunction(() => {
      const node = document.querySelector<HTMLElement>(".transcript");
      return node && node.scrollHeight - node.scrollTop - node.clientHeight <= 1;
    });
    assert.equal(await page.getByRole("button", { name: "Jump to latest" }).count(), 0);

    await conversation.hover();
    await page.mouse.wheel(0, -240);
    await page.getByRole("button", { name: "Jump to latest" }).waitFor();
    const anchorBefore = await conversation.evaluate((node) => {
      const top = node.getBoundingClientRect().top;
      const first = [...node.querySelectorAll<HTMLElement>("[data-message]")].find(
        (item) => item.getBoundingClientRect().bottom > top,
      )!;
      return { key: first.dataset.message!, top: first.getBoundingClientRect().top };
    });
    model.enqueue({ text: lines("output-after-user-scroll") });
    const receipt = await sessions.submitMessage(sessionId, {
      submissionId: randomUUID(),
      expectedHistoryRevision: 0,
      text: "append while reading",
      llmSelection: { connection, modelId: "fixture-a", reasoningEffort: null },
    });
    await waitForSnapshot(
      sessions,
      sessionId,
      (snapshot) => snapshot.lastSettlement?.executionId === receipt.executionId,
      "the append while reading to settle",
    );
    await page
      .locator(`[data-message^="${receipt.executionId}/"][data-message$="/text"]`)
      .waitFor();
    const anchorAfter = await conversation
      .locator(`[data-message="${anchorBefore.key}"]`)
      .evaluate((node) => node.getBoundingClientRect().top);
    assert.ok(Math.abs(anchorAfter - anchorBefore.top) <= 2);
    await page.getByRole("button", { name: "Jump to latest" }).click();
    await page.getByText("output-after-user-scroll line 29", { exact: false }).waitFor();
    assert.deepEqual(pageErrors, []);
  });
});

void test("tab changes limit session reads and preserve the reading anchor during a stream", async () => {
  await withOfflineBrowser(async ({ model, server, page }) => {
    const snapshotReads = new Map<string, number>();
    const snapshotResponses = new Map<string, number>();
    const browserSnapshots = new Map<string, SessionSnapshot>();
    const eventUpdates: { open: string[]; watched: string[] }[] = [];
    let eventStreams = 0;
    let sessionCreates = 0;
    let messageSubmissions = 0;
    let stopRequests = 0;
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    page.on("request", (request) => {
      const url = new URL(request.url());
      const method = request.method();
      if (url.pathname === "/api/sessions" && method === "POST") sessionCreates++;
      if (url.pathname === "/api/events" && method === "GET") eventStreams++;
      if (method === "POST" && /^\/api\/events\/[0-9a-f-]+$/i.test(url.pathname)) {
        const update = request.postDataJSON() as { open: string[]; watched: string[] };
        eventUpdates.push(update);
      }
      if (url.pathname.endsWith("/message-submissions") && method === "POST") messageSubmissions++;
      if (method === "POST" && /\/executions\/[^/]+\/stop$/.test(url.pathname)) stopRequests++;
      const snapshot = url.pathname.match(/^\/api\/sessions\/([^/]+)\/snapshot$/);
      if (snapshot && method === "GET")
        snapshotReads.set(snapshot[1]!, (snapshotReads.get(snapshot[1]!) ?? 0) + 1);
    });
    page.on("response", (response) => {
      const url = new URL(response.url());
      const snapshot = url.pathname.match(/^\/api\/sessions\/([^/]+)\/snapshot$/);
      if (!snapshot || response.request().method() !== "GET") return;
      void response
        .json()
        .then((value: SessionSnapshot) => {
          browserSnapshots.set(snapshot[1]!, value);
          snapshotResponses.set(snapshot[1]!, (snapshotResponses.get(snapshot[1]!) ?? 0) + 1);
        })
        .catch(() => {});
    });

    await page.goto(server.origin.href);
    const message = page.getByRole("textbox", { name: "Message" });
    await message.waitFor();
    const modelPicker = page.getByRole("button", { name: /^Model:/ });
    await modelPicker.click();
    await page.getByRole("option", { name: "fixture-a" }).click();
    const send = page.getByRole("button", { name: "Send" });
    const sessions = new SessionClient(server.origin);

    model.enqueue({
      text: `bootstrap-anchor\n${Array.from(
        { length: 16 },
        (_, index) => `history line ${index} ${"context ".repeat(4)}`,
      ).join("\n")}`,
    });
    await message.fill("initialize transcript");
    await send.click();
    await page.getByText("bootstrap-anchor", { exact: false }).waitFor();
    await page.getByText("history line 15", { exact: false }).waitFor();
    assert.equal(messageSubmissions, 1);
    assert.equal(model.calls.length, 1);

    const firstSessionId = new URL(page.url()).hash.slice(1);
    await page.getByRole("button", { name: "New chat" }).click();
    await page.waitForFunction(() => document.querySelectorAll('[role="tab"]').length === 2);
    await page.waitForFunction((id) => location.hash.slice(1) !== id, firstSessionId);
    const secondSessionId = new URL(page.url()).hash.slice(1);
    const secondTab = page.getByRole("tab").nth(1);
    await page.getByRole("tab").first().click();
    await page.waitForFunction((id) => location.hash.slice(1) === id, firstSessionId);

    const streamedText =
      Array.from(
        { length: 12 },
        (_, index) => `stream-line-${index} ${"reading content ".repeat(3)}`,
      ).join("\n") + "\nstream-tail-marker";
    let holdNextSnapshot = false;
    let continueSnapshot!: () => void;
    let snapshotWasHeld!: () => void;
    const snapshotMayContinue = new Promise<void>((resolve) => {
      continueSnapshot = resolve;
    });
    const snapshotHoldStarted = new Promise<void>((resolve) => {
      snapshotWasHeld = resolve;
    });
    page.on("close", continueSnapshot);
    await page.route(`**/api/sessions/${firstSessionId}/snapshot`, async (route) => {
      if (!holdNextSnapshot) return route.continue();
      holdNextSnapshot = false;
      snapshotWasHeld();
      await snapshotMayContinue;
      await route.continue();
    });
    model.enqueue({ text: streamedText, delayMs: 500, chunkSize: 40 });
    const previousSnapshotReads = snapshotReads.get(firstSessionId) ?? 0;
    await message.fill("start a streamed execution");
    await send.click();
    await page.getByRole("button", { name: "Stop" }).waitFor();
    holdNextSnapshot = true;
    await snapshotHoldStarted;
    await new Promise((resolve) => setTimeout(resolve, 600));
    continueSnapshot();
    await waitForCondition(
      () => (snapshotReads.get(firstSessionId) ?? 0) >= previousSnapshotReads + 2,
      "the event queued behind an in-flight snapshot read",
    );
    await page.getByText("stream-line-1 reading", { exact: false }).waitFor();

    const conversation = page.getByRole("region", { name: "Conversation" });
    await conversation.evaluate((node) => {
      node.scrollTop = 0;
    });
    await page.getByRole("button", { name: "Jump to latest" }).waitFor();
    const readingAnchor = page.getByText("bootstrap-anchor", { exact: false });
    const anchorBox = await readingAnchor.boundingBox();
    const conversationBox = await conversation.boundingBox();
    assert.ok(anchorBox);
    assert.ok(conversationBox);
    await conversation.evaluate(
      (node, top) =>
        new Promise<void>((resolve) => {
          const target = Math.max(0, top - node.getBoundingClientRect().top - 12);
          const finish = () => {
            node.removeEventListener("scroll", onScroll);
            requestAnimationFrame(() => resolve());
          };
          const onScroll = () => finish();
          if (node.scrollTop === target) finish();
          else {
            node.addEventListener("scroll", onScroll, { once: true });
            node.scrollTop = target;
          }
        }),
      anchorBox.y,
    );
    await page.getByRole("button", { name: "Jump to latest" }).waitFor();
    const anchorBefore = await readingAnchor.boundingBox();
    const scrollBefore = await conversation.evaluate((node) => node.scrollTop);
    assert.ok(anchorBefore);
    assert.ok(scrollBefore > 0);

    const secondSessionReads = snapshotReads.get(secondSessionId) ?? 0;
    await secondTab.click();
    await page.waitForFunction((id) => location.hash.slice(1) === id, secondSessionId);
    await waitForCondition(
      () => (snapshotReads.get(secondSessionId) ?? 0) > secondSessionReads,
      "the newly active session snapshot",
    );
    await waitForCondition(
      () =>
        eventUpdates.some(
          (update) =>
            update.open.length === 2 &&
            update.open.includes(firstSessionId) &&
            update.open.includes(secondSessionId) &&
            update.watched.length === 1 &&
            update.watched[0] === secondSessionId,
        ),
      "the active-only watched event subscription",
    );
    const inactiveSnapshotReads = snapshotReads.get(firstSessionId) ?? 0;
    await waitForQuietCount(
      () => snapshotReads.get(firstSessionId) ?? 0,
      500,
      3_000,
      "obsolete snapshot polling to stop",
    );
    assert.equal(snapshotReads.get(firstSessionId) ?? 0, inactiveSnapshotReads);

    const runningAway = await sessions.getSnapshot(firstSessionId, AbortSignal.timeout(5_000));
    assert.ok(runningAway.unsettledExecutionId);
    assert.equal(stopRequests, 0);
    assert.equal(model.calls[1]?.cancelled, false);

    const responsesBeforeReturn = snapshotResponses.get(firstSessionId) ?? 0;
    await page.getByRole("tab").first().click();
    await page.waitForFunction((id) => location.hash.slice(1) === id, firstSessionId);
    await waitForCondition(
      () => (snapshotResponses.get(firstSessionId) ?? 0) > responsesBeforeReturn,
      "the reactivated session snapshot",
    );
    await waitForCondition(
      () =>
        eventUpdates.some(
          (update) =>
            update.open.length === 2 &&
            update.open.includes(firstSessionId) &&
            update.open.includes(secondSessionId) &&
            update.watched.length === 1 &&
            update.watched[0] === firstSessionId,
        ),
      "the reactivated watched event subscription",
    );
    const runningOnReturn = browserSnapshots.get(firstSessionId);
    assert.equal(runningOnReturn?.unsettledExecutionId, runningAway.unsettledExecutionId);
    const anchorAfterReturn = await readingAnchor.boundingBox();
    assert.ok(anchorAfterReturn);
    assert.ok(
      Math.abs(anchorAfterReturn.y - anchorBefore.y) <= 2,
      `Reading anchor moved on tab return: ${JSON.stringify({
        anchorBefore,
        anchorAfterReturn,
        scrollBefore,
        scrollAfter: await conversation.evaluate((node) => ({
          top: node.scrollTop,
          height: node.scrollHeight,
          client: node.clientHeight,
        })),
      })}`,
    );

    const runningExecutionId = runningOnReturn?.unsettledExecutionId;
    assert.ok(runningExecutionId);
    await waitForSnapshot(
      sessions,
      firstSessionId,
      (snapshot) =>
        snapshot.unsettledExecutionId === null &&
        snapshot.lastSettlement?.executionId === runningExecutionId,
      "the streamed execution to finish",
    );
    await waitForCondition(() => {
      const snapshot = browserSnapshots.get(firstSessionId);
      return (
        snapshot?.unsettledExecutionId === null &&
        snapshot.lastSettlement?.executionId === runningExecutionId
      );
    }, "the mounted transcript to observe the settled stream");
    await page.getByText("Running…", { exact: true }).waitFor({ state: "detached" });
    const anchorAfterStream = await readingAnchor.boundingBox();
    assert.ok(anchorAfterStream);
    assert.ok(
      Math.abs(anchorAfterStream.y - anchorBefore.y) <= 2,
      `Reading anchor moved during the stream: ${JSON.stringify({
        anchorBefore,
        anchorAfterStream,
        scrollBefore,
        scrollAfter: await conversation.evaluate((node) => ({
          top: node.scrollTop,
          height: node.scrollHeight,
          client: node.clientHeight,
        })),
      })}`,
    );
    await page.getByRole("button", { name: "Jump to latest" }).waitFor();
    assert.equal(stopRequests, 0);
    assert.equal(sessionCreates, 2);
    assert.ok(eventStreams > 0);
    assert.equal(messageSubmissions, 2);
    assert.equal(model.calls.length, 2);
    assert.ok(model.calls.every((call) => !call.cancelled));
    assert.deepEqual(pageErrors, []);
  });
});
