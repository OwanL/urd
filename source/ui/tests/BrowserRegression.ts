import assert from "node:assert/strict";
import test from "node:test";
import { chromium, type Page } from "playwright";
import { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { startLocalModelServer } from "./LocalModelServer.ts";
import { ServerProcess } from "./ServerProcess.ts";

type ModelFixture = Awaited<ReturnType<typeof startLocalModelServer>>;

async function withOfflineBrowser(
  run: (fixture: { model: ModelFixture; server: ServerProcess; page: Page }) => Promise<void>,
) {
  const model = await startLocalModelServer();
  let server: ServerProcess | undefined;
  let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
  try {
    const runningServer = await ServerProcess.start();
    server = runningServer;
    await model.install(runningServer.origin);
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1_024, height: 480 } });
    page.setDefaultTimeout(10_000);
    await run({ model, server: runningServer, page });
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
    let sessionCreates = 0;
    let messageSubmissions = 0;
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    page.on("request", (request) => {
      const url = new URL(request.url());
      if (url.pathname === "/api/sessions" && request.method() === "GET") sessionLists++;
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
    assert.equal(sessionLists, 1);
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

void test("tab changes stop obsolete polling while preserving the reading anchor during a stream", async () => {
  await withOfflineBrowser(async ({ model, server, page }) => {
    const snapshotReads = new Map<string, number>();
    const snapshotResponses = new Map<string, number>();
    const browserSnapshots = new Map<string, SessionSnapshot>();
    let sessionCreates = 0;
    let messageSubmissions = 0;
    let stopRequests = 0;
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));
    page.on("request", (request) => {
      const url = new URL(request.url());
      const method = request.method();
      if (url.pathname === "/api/sessions" && method === "POST") sessionCreates++;
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
    model.enqueue({ text: streamedText, delayMs: 500, chunkSize: 40 });
    const previousSnapshotReads = snapshotReads.get(firstSessionId) ?? 0;
    await message.fill("start a streamed execution");
    await send.click();
    await page.getByText("Running…", { exact: true }).waitFor();
    await waitForCondition(
      () => (snapshotReads.get(firstSessionId) ?? 0) >= previousSnapshotReads + 2,
      "active-session snapshot polling",
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
    await conversation.evaluate((node, top) => {
      node.scrollTop = Math.max(0, top - node.getBoundingClientRect().top - 12);
    }, anchorBox.y);
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
    const runningOnReturn = browserSnapshots.get(firstSessionId);
    assert.equal(runningOnReturn?.unsettledExecutionId, runningAway.unsettledExecutionId);
    const anchorAfterReturn = await readingAnchor.boundingBox();
    assert.ok(anchorAfterReturn);
    assert.ok(Math.abs(anchorAfterReturn.y - anchorBefore.y) <= 2);

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
    assert.ok(Math.abs(anchorAfterStream.y - anchorBefore.y) <= 2);
    await page.getByRole("button", { name: "Jump to latest" }).waitFor();
    assert.equal(stopRequests, 0);
    assert.equal(sessionCreates, 2);
    assert.equal(messageSubmissions, 2);
    assert.equal(model.calls.length, 2);
    assert.ok(model.calls.every((call) => !call.cancelled));
    assert.deepEqual(pageErrors, []);
  });
});
