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
    if (request.url !== "/viewer-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/BrowserPageViewFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage();
  await page.goto(`${server.resolvedUrls!.local[0]}viewer-test`);
  await page.locator("canvas").waitFor();
  await page.waitForFunction(() => (window as unknown as { viewerReady?: boolean }).viewerReady);
  await page.locator("canvas").evaluate((canvas: HTMLCanvasElement) => {
    canvas.width = 200;
    canvas.height = 100;
    canvas.style.width = "100px";
    canvas.style.height = "50px";
    canvas.setPointerCapture = () => {};
  });
});

after(async () => {
  await browser?.close();
  await server?.close();
});

async function commands() {
  return page.evaluate(() =>
    (window as unknown as { viewerCommands: { type: string }[] }).viewerCommands
      .splice(0)
      .filter((command) => command.type !== "resize"),
  );
}

async function pointer(type: string, x: number, y: number, pointerType = "touch", pointerId = 1) {
  await page.locator("canvas").evaluate(
    (canvas, args) => {
      const bounds = canvas.getBoundingClientRect();
      canvas.dispatchEvent(
        new PointerEvent(args.type, {
          bubbles: true,
          cancelable: true,
          pointerId: args.pointerId,
          pointerType: args.pointerType,
          button: 0,
          clientX: bounds.left + args.x,
          clientY: bounds.top + args.y,
        }),
      );
    },
    { type, x, y, pointerType, pointerId },
  );
}

void test("pointer taps map to frame coordinates and clamp outside the canvas", async () => {
  await commands();
  await pointer("pointerdown", 20, 10, "mouse");
  await pointer("pointerup", 22, 12, "mouse");
  assert.deepEqual(await commands(), [{ type: "click", x: 44, y: 24 }]);
  await pointer("pointerdown", 99, 49);
  await pointer("pointerup", 103, 52);
  assert.deepEqual(await commands(), [{ type: "click", x: 199, y: 99 }]);
});

void test("touch drag scrolls, while mouse drag and cancelled gestures do not click", async () => {
  await pointer("pointerdown", 40, 30);
  await pointer("pointermove", 30, 20);
  await pointer("pointerup", 30, 20);
  assert.deepEqual(await commands(), [{ type: "wheel", dx: 20, dy: 20, x: 60, y: 40 }]);
  await pointer("pointerdown", 40, 30, "mouse");
  await pointer("pointermove", 30, 20, "mouse");
  await pointer("pointerup", 30, 20, "mouse");
  await pointer("pointerdown", 20, 10);
  await pointer("pointercancel", 20, 10);
  await pointer("pointerup", 20, 10);
  assert.deepEqual(await commands(), []);
});

void test("Tab retains native focus navigation instead of being forwarded", async () => {
  await page.getByRole("application").focus();
  await page.keyboard.press("Tab");
  assert.equal(
    await page
      .getByLabel("Text for Example")
      .evaluate((element) => element === document.activeElement),
    true,
  );
  assert.deepEqual(await commands(), []);
});

void test("AltGr printable characters are forwarded once, but shortcuts remain local", async () => {
  const result = await page.getByRole("application").evaluate((surface) => {
    const dispatch = (key: string, altGraph: boolean) => {
      const event = new KeyboardEvent("keydown", {
        key,
        ctrlKey: true,
        altKey: true,
        bubbles: true,
        cancelable: true,
      });
      Object.defineProperty(event, "getModifierState", {
        value: (name: string) => name === "AltGraph" && altGraph,
      });
      surface.dispatchEvent(event);
      return event.defaultPrevented;
    };
    return [dispatch("@", true), dispatch("a", false)];
  });
  assert.deepEqual(result, [true, false]);
  assert.deepEqual(await commands(), [{ type: "text", text: "@" }]);
});
