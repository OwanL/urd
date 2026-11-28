import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";

let server: ViteDevServer;
let browser: Browser;
let page: Page;

before(async () => {
  server = await createServer({ configFile: false, server: { port: 0 }, appType: "custom" });
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/provider-setup-removal-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/ProviderSetupRemovalFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 720, height: 700 } });
});

after(async () => {
  await browser?.close();
  await server?.close();
});

void test("removes an unfinished provider setup after confirmation", async () => {
  await page.goto(`${server.resolvedUrls!.local[0]}provider-setup-removal-test`);

  const remove = page.getByRole("button", { name: "Remove saved setup for OpenAI" });
  assert.equal(await remove.isVisible(), true);
  await remove.click();

  const confirmation = page.getByRole("group", { name: "Confirm connection change" });
  assert.match((await confirmation.textContent()) ?? "", /Remove unfinished setup for “OpenAI”/);
  await page.getByRole("button", { name: "Confirm setup removal" }).click();

  assert.equal(
    await page.getByRole("status", { name: "Saved connection count" }).textContent(),
    "0",
  );
  assert.equal(await remove.count(), 0);
});
