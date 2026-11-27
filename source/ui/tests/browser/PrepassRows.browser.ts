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
    if (request.url !== "/prepass-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/PrepassRowsFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage();
  await page.goto(`${server.resolvedUrls!.local[0]}prepass-test`);
  await page.waitForFunction(() => "showPrepass" in window);
});

after(async () => {
  await browser?.close();
  await server?.close();
});

async function show(state: string, length: number, key: string) {
  await page.evaluate(
    ({ state, length, key }) => {
      (
        window as unknown as { showPrepass: (state: string, length: number, key: string) => void }
      ).showPrepass(state, length, key);
    },
    { state, length, key },
  );
}

for (const length of [1, 100]) {
  void test(`completion keeps geometry and disclosure stable with ${length} earlier rows`, async () => {
    await show("running", length, `live-${length}`);
    const toggle = page.locator(".response-status-toggle");
    const height = () =>
      page
        .locator(".response-status-rows")
        .evaluate((element) => element.getBoundingClientRect().height);
    const openHeight = await height();
    assert.equal(await toggle.getAttribute("aria-expanded"), "true");
    assert.equal(await page.locator(".response-status-row").count(), 4);
    const before = await page
      .locator("#following")
      .evaluate((element) => element.getBoundingClientRect().top);
    await page.evaluate(() => {
      const group = document.querySelector(".response-status-rows")!;
      const changes: number[] = [];
      (window as unknown as { changes: number[] }).changes = changes;
      const observer = new ResizeObserver(() => changes.push(group.getBoundingClientRect().height));
      observer.observe(group);
      (window as unknown as { resizeObserver: ResizeObserver }).resizeObserver = observer;
    });
    await page.waitForTimeout(40);
    await show("completed", length, `live-${length}`);
    await page.waitForTimeout(100);
    const after = await page
      .locator("#following")
      .evaluate((element) => element.getBoundingClientRect().top);
    assert.equal(after, before);
    const changes = await page.evaluate(() => {
      (window as unknown as { resizeObserver: ResizeObserver }).resizeObserver.disconnect();
      return (window as unknown as { changes: number[] }).changes;
    });
    assert.equal(new Set(changes).size, 1);
    assert.equal(await toggle.getAttribute("aria-expanded"), "true");
    await toggle.click();
    assert.equal(await toggle.getAttribute("aria-expanded"), "false");
    const collapsedHeight = await height();
    assert.ok(collapsedHeight < openHeight);
    await show("running", length, `live-${length}`);
    await show("completed", length, `live-${length}`);
    assert.equal(await toggle.getAttribute("aria-expanded"), "false");
    assert.equal(await height(), collapsedHeight);
    await toggle.click();
    assert.equal(await page.locator(".response-status-row").count(), 4);
    assert.equal(await height(), openHeight);
    await show("completed", length, `history-${length}`);
    assert.equal(await toggle.getAttribute("aria-expanded"), "false");
    assert.equal(await page.locator(".response-status-row").count(), 0);
    assert.equal(await height(), collapsedHeight);
  });
}
