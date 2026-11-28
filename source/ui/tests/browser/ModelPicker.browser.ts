import assert from "node:assert/strict";
import { after, before, beforeEach, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";

let server: ViteDevServer;
let browser: Browser;
let page: Page;

const trigger = () => page.getByRole("button", { name: "Model: Choose model" });
const search = () => page.getByRole("combobox", { name: "Search models" });
const selection = () => page.getByRole("status", { name: "Selected model" });

before(async () => {
  server = await createServer({ configFile: false, server: { port: 0 }, appType: "custom" });
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/model-picker-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/ModelPickerFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 600, height: 600 } });
});

beforeEach(async () => {
  await page.goto(`${server.resolvedUrls!.local[0]}model-picker-test`);
  await trigger().waitFor();
});

after(async () => {
  await browser?.close();
  await server?.close();
});

void test("Enter does not choose the first model before the user moves or searches", async () => {
  await trigger().click();
  await search().press("Enter");
  assert.equal(await selection().textContent(), "");
  assert.equal(await search().isVisible(), true);
});

void test("qualifies an ambiguous selected model with its provider", async () => {
  await trigger().click();
  await search().press("ArrowDown");
  await search().press("Enter");
  assert.equal(
    await page.getByRole("button", { name: "Model: Model A · Provider One" }).textContent(),
    "Model A · Provider One",
  );
});

void test("searches catalog model identifiers and reveals the matching identifier", async () => {
  await trigger().click();
  await search().fill("20261128");
  const option = page.getByRole("option", { name: /Claude Haiku 4\.5/ });
  assert.equal(await option.isVisible(), true);
  assert.match((await option.textContent()) ?? "", /claude-haiku-4\.5-20261128/);
});

void test("Home and End navigate the filtered model list", async () => {
  await trigger().click();
  await search().press("End");
  await search().press("Enter");
  assert.equal(await selection().textContent(), "two/model-c");
});
