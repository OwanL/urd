import assert from "node:assert/strict";
import test from "node:test";
import { chromium } from "playwright";
import { createServer } from "vite";

void test("warning is separate from Compact and a failed refresh removes it", async (t) => {
  const server = await createServer({ server: { port: 0 }, appType: "custom" });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/cache-indicators") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/PromptCacheIndicatorsFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  let reads = 0;
  await page.route("**/api/usage/prompt-cache*", (route) => {
    reads++;
    if (reads === 2) return route.fulfill({ status: 500, body: "error" });
    const totals = {
      inputTokens: 10000,
      cachedInputTokens: 2000,
      reportedRequests: 2,
      totalRequests: 2,
    };
    return route.fulfill({ json: { lastTurn: totals, session: totals, warning: totals } });
  });
  await page.goto(`${server.resolvedUrls!.local[0]}cache-indicators`);
  const warning = page.getByRole("button", { name: "Low prompt cache reuse" });
  await warning.click();
  await page.getByRole("dialog", { name: "Low prompt cache reuse" }).waitFor();
  assert.equal(await page.locator("output").innerText(), "Compactions: 0");
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: /Context window:.*Compact/ }).click();
  assert.equal(await page.locator("output").innerText(), "Compactions: 1");
  await warning.click();
  const failedRefresh = page.waitForResponse(
    (response) => response.url().includes("/api/usage/prompt-cache") && response.status() === 500,
  );
  await page.getByRole("button", { name: "Refresh" }).click();
  await failedRefresh;
  await warning.waitFor({ state: "detached" });
  await page.getByRole("button", { name: "Refresh" }).click();
  await warning.waitFor();
  assert.equal(await page.getByRole("dialog", { name: "Low prompt cache reuse" }).count(), 0);
  assert.equal(reads, 3);
});
