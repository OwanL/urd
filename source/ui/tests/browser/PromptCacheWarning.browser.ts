import assert from "node:assert/strict";
import test from "node:test";
import { chromium } from "playwright";
import { createServer } from "vite";

void test("a qualified prompt-cache warning shows its measured details for the subagent session", async (t) => {
  const server = await createServer({ server: { port: 0 }, appType: "custom" });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/prompt-cache-warning-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/SubagentViewFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  const child = "00000000-0000-4000-8000-000000000002";
  await page.route(`**/api/sessions/${child}/subagent-view`, (route) =>
    route.fulfill({
      json: {
        parentId: "parent",
        title: "Child",
        historyRevision: 0,
        live: true,
        modelId: "fixture-model",
        reasoningEffort: "high",
        contextWindow: 128_000,
        contextUsage: { usedTokens: 4_000, source: "measured" },
        workingTime: {
          completedMilliseconds: 12_000,
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
  let promptCacheReads = 0;
  await page.route("**/api/usage/prompt-cache*", async (route) => {
    assert.equal(new URL(route.request().url()).searchParams.get("sessionId"), child);
    promptCacheReads++;
    await route.fulfill({
      json: {
        lastTurn: {
          inputTokens: 4_000,
          cachedInputTokens: 2_000,
          reportedRequests: 1,
          totalRequests: 1,
        },
        session: {
          inputTokens: 10_000,
          cachedInputTokens: 4_000,
          reportedRequests: 2,
          totalRequests: 2,
        },
        warning: {
          inputTokens: 8_000,
          cachedInputTokens: 2_000,
          reportedRequests: 2,
          totalRequests: 2,
        },
      },
    });
  });

  await page.goto(`${server.resolvedUrls!.local[0]}prompt-cache-warning-test`);
  const promptCacheResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return (
      url.pathname === "/api/usage/prompt-cache" && url.searchParams.get("sessionId") === child
    );
  });
  await page.getByRole("button", { name: "Open child" }).click();
  await promptCacheResponse;
  await page.getByRole("navigation", { name: "Conversation path" }).waitFor({ timeout: 5_000 });
  await page.getByRole("button", { name: "Context window: 4k/128k" }).waitFor({ timeout: 5_000 });
  const warning = page.getByRole("button", { name: "Low prompt cache reuse" });
  assert.equal(promptCacheReads, 1, "prompt-cache details are fetched once for the child session");
  await warning.waitFor({ timeout: 5_000 });

  await warning.click();
  const details = page.getByRole("dialog", { name: "Low prompt cache reuse" });
  await details.getByText("25.0% cached after the first request").waitFor({ timeout: 5_000 });
  await details.getByText("2 measured requests · warning below 60%").waitFor();
  await details.getByText("Last turn", { exact: true }).waitFor();
  await details.getByText("50.0% cached · 2,000 uncached", { exact: true }).waitFor();
  await details.getByText("Session", { exact: true }).waitFor();
  await details.getByText("40.0% cached · 6,000 uncached", { exact: true }).waitFor();

  assert.equal(promptCacheReads, 1, "opening the warning does not issue another detail request");
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Context window: 4k/128k" }).hover();
  await page.getByRole("tooltip").getByText("Last turn", { exact: true }).waitFor();
  assert.equal(
    promptCacheReads,
    1,
    "showing context details reuses the fetched prompt-cache summary",
  );
});
