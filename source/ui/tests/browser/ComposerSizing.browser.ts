import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";

// The composer input is autosized by CSS field-sizing where available and by
// a JS resize fallback elsewhere. These checks cover the size lifecycle only:
// blank initial height, growth with content, the max-height cap, and the
// return to blank height after clearing.

let server: ViteDevServer;
let browser: Browser;
let page: Page;

const input = () => page.locator('textarea[aria-label="Message"]');
// min-height 45px from the style sheet; max-height min(35dvh, 14rem) = 210px at a 600px viewport.
const blankHeight = 45;
const capped = 210;

async function height() {
  return (await input().evaluate((element) => element.getBoundingClientRect().height)) as number;
}

before(async () => {
  server = await createServer({ configFile: false, server: { port: 0 }, appType: "custom" });
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/composer-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/ComposerSizingFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 600, height: 600 } });
  await page.goto(`${server.resolvedUrls!.local[0]}composer-test`);
  await page.waitForSelector('textarea[aria-label="Message"]');
});

after(async () => {
  await browser?.close();
  await server?.close();
});

void test("composer stays blank-height initially and grows back after clearing", async () => {
  assert.ok(Math.abs((await height()) - blankHeight) < 1, "blank height off");
  await input().fill("hello");
  assert.ok(Math.abs((await height()) - blankHeight) < 1, "one line changed the blank height");
  await input().fill("");
  assert.ok(
    Math.abs((await height()) - blankHeight) < 1,
    "clearing did not return to blank height",
  );
});

void test("composer grows with content and is managed without inline JS heights", async () => {
  const supportsFieldSizing = await page.evaluate(() => CSS.supports("field-sizing", "content"));
  const lines = Array.from({ length: 6 }, (_, i) => `line ${i + 1}`).join("\n");
  await input().fill(lines);
  assert.ok((await height()) > blankHeight + 40, "did not grow with content");
  const inlineHeight = await input().evaluate((element) => element.style.height);
  if (supportsFieldSizing) assert.equal(inlineHeight, "", "JS resize ran");
  else assert.match(inlineHeight, /^[\d.]+px$/, "JS fallback did not size the input");
});

void test("composer caps at max-height and becomes scrollable", async () => {
  const lines = Array.from({ length: 40 }, (_, i) => `line ${i + 1}`).join("\n");
  await input().fill(lines);
  const size = await input().evaluate((element) => ({
    height: element.getBoundingClientRect().height,
    scrollHeight: element.scrollHeight,
    clientHeight: element.clientHeight,
    scrollTop: element.scrollTop,
  }));
  assert.ok(Math.abs(size.height - capped) < 1, `not capped at ${capped}`);
  assert.ok(size.scrollHeight > size.clientHeight + 1, "content is not overflowing");
  // Scrolling to the last line must be possible within the capped viewport.
  await input().evaluate((element) => {
    element.scrollTop = element.scrollHeight;
  });
  const scrolled = await input().evaluate((element) => element.scrollTop);
  assert.ok(scrolled > 50, "input is not scrollable");
  await input().fill("");
});

void test("composer rewraps and regrows when the width changes", async () => {
  await page.setViewportSize({ width: 600, height: 600 });
  await input().fill("a long line of text that will wrap more when the viewport narrows");
  const wide = await height();
  await page.setViewportSize({ width: 200, height: 600 });
  const narrow = await height();
  await page.setViewportSize({ width: 600, height: 600 });
  const restored = await height();
  assert.ok(narrow > wide, "narrowing did not increase the height");
  assert.ok(Math.abs(restored - wide) < 1, "widening did not restore the height");
  await input().fill("");
});
