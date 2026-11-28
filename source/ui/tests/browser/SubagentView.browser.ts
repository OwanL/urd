import assert from "node:assert/strict";
import test from "node:test";
import { chromium } from "playwright";
import { createServer } from "vite";

void test("a subagent view shows its own session details without allowing messages", async (t) => {
  const server = await createServer({ server: { port: 0 }, appType: "custom" });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/subagent-view-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/SubagentViewFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage({ viewport: { width: 660, height: 600 } });
  const child = "00000000-0000-4000-8000-000000000002";
  let live = true;
  await page.route(`**/api/sessions/${child}/subagent-view`, (route) =>
    route.fulfill({
      json: {
        parentId: "parent",
        title: "Child",
        historyRevision: 0,
        live,
        modelId: "fixture-model",
        reasoningEffort: "high",
        contextWindow: 128_000,
        contextUsage: { usedTokens: live ? 4_000 : 8_000, source: "measured" },
        workingTime: {
          completedMilliseconds: live ? 12_000 : 13_000,
          startedAt: null,
          hasUnknownDuration: false,
        },
        admissionFenced: false,
      },
    }),
  );
  await page.route(`**/api/sessions/${child}/history?*`, (route) =>
    route.fulfill({
      json: {
        sessionId: child,
        historyRevision: 0,
        entries: [],
        toolLabels: {},
        settlements: [],
        hasBefore: false,
        hasAfter: false,
      },
    }),
  );
  await page.route("**/api/sessions/parent/user-questions", (route) => route.fulfill({ json: [] }));
  const usageRequests: (string | null)[] = [];
  await page.route("**/api/usage?*", (route) => {
    usageRequests.push(new URL(route.request().url()).searchParams.get("sessionId"));
    return route.fulfill({
      json: {
        totals: {
          costUsd: 0.42,
          requests: 1,
          estimatedRequests: 0,
          unknownRequests: 0,
          activeRequests: 0,
        },
        breakdown: [],
        history: [],
        recent: [],
      },
    });
  });
  await page.route(`**/api/usage/prompt-cache?sessionId=${child}`, (route) => {
    const totals = {
      inputTokens: 4_000,
      cachedInputTokens: 2_000,
      reportedRequests: 1,
      totalRequests: 1,
    };
    return route.fulfill({ json: { lastTurn: totals, session: totals } });
  });
  let writes = 0;
  page.on("request", (request) => {
    if (request.method() !== "GET" && new URL(request.url()).pathname.startsWith("/api/")) writes++;
  });

  await page.goto(`${server.resolvedUrls!.local[0]}subagent-view-test`);
  await page.getByRole("button", { name: "Open child" }).click();
  await page.getByRole("navigation", { name: "Conversation path" }).waitFor({ timeout: 5_000 });
  const input = page.getByRole("textbox", { name: "Message", exact: true });
  await input.waitFor({ timeout: 5_000 });
  assert.equal(await input.isDisabled(), true);
  assert.equal(await page.getByRole("button", { name: "Send", exact: true }).isDisabled(), true);
  await page.getByText("fixture-model", { exact: true }).waitFor({ timeout: 5_000 });
  await page.getByRole("button", { name: "Agent working time: 12s" }).waitFor({ timeout: 5_000 });
  await page
    .getByRole("button", { name: "Context window: 4k/128k", exact: true })
    .waitFor({ timeout: 5_000 });
  const cost = page.getByRole("button", { name: "Session API-equivalent cost: $0.42" });
  await cost.waitFor({ timeout: 5_000 });
  assert.ok(usageRequests.length > 0 && usageRequests.every((id) => id === child));

  live = false;
  const context = page.getByRole("button", { name: "Context window: 8k/128k", exact: true });
  await context.waitFor({ timeout: 5_000 });
  await page.getByRole("button", { name: "Agent working time: 13s" }).waitFor({ timeout: 5_000 });
  await cost.click();
  await page
    .getByRole("dialog", { name: "Usage details: this session" })
    .waitFor({ timeout: 5_000 });
  await page.keyboard.press("Escape");
  await context.hover();
  await page.getByRole("tooltip").getByText("Last turn").waitFor({ timeout: 5_000 });

  await page.setViewportSize({ width: 180, height: 600 });
  await page.getByRole("button", { name: "More options" }).click();
  const details = page.getByRole("dialog", { name: "Session details" });
  await details
    .getByRole("button", { name: "Context window: 8k/128k", exact: true })
    .waitFor({ timeout: 5_000 });
  await page.getByRole("button", { name: "Parent chat" }).click();
  assert.equal(await input.count(), 0);
  assert.ok(usageRequests.every((id) => id === child));
  assert.equal(writes, 0);
});
