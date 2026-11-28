import assert from "node:assert/strict";
import test from "node:test";
import { chromium } from "playwright";
import { createServer } from "vite";

void test("a visible subagent row picks up its title while the call is still running", async (t) => {
  const server = await createServer({ server: { port: 0 }, appType: "custom" });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/subagent-title-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/SubagentTitleFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  await page.clock.install();
  let title: string | null = null;
  let reads = 0;
  await page.route("**/api/sessions/parent/subagents/by-call/request/call", async (route) => {
    reads++;
    await route.fulfill({
      json: { id: "child", parentId: "parent", title: title ?? "Subagent", titlePending: !title },
    });
  });
  await page.goto(`${server.resolvedUrls!.local[0]}subagent-title-test`);
  await page.getByRole("button", { name: "Subagent", exact: true }).focus({ timeout: 5_000 });

  title = "Fixture child";
  const renamed = page.getByRole("button", { name: title, exact: true });
  await renamed.waitFor({ timeout: 5_000 });
  assert.equal(await renamed.evaluate((button) => document.activeElement === button), true);

  const settledReads = reads;
  await page.clock.runFor(5_000);
  assert.equal(reads, settledReads, "a final title no longer needs polling");
});
