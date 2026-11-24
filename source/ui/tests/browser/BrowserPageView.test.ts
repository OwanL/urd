import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import type { BrowserPage } from "../../chat/browser/BrowserPage.ts";

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
    (window as unknown as { viewerCommands: { type: string }[] }).viewerCommands.splice(0),
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

interface ViewerWindow {
  viewerStream: {
    open(): void;
    close(): void;
    frame(blob: Blob): void;
  };
  viewerDecode: {
    stallNext(): void;
    release(): void;
    reset(): void;
    stats(): { decodeRequests: number; inFlight: number; peakConcurrent: number };
  };
  viewerShowPage(page: BrowserPage): void;
}

async function decodeStats() {
  return page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.stats());
}

// Paints a solid block into a fresh canvas and hands the encoded blob to the
// page stream, exactly as the socket would deliver it.
async function deliverFrame(fill: string): Promise<void> {
  await page.evaluate((source) => {
    const image = document.createElement("canvas");
    image.width = 4;
    image.height = 4;
    const context = image.getContext("2d")!;
    context.fillStyle = source;
    context.fillRect(0, 0, 4, 4);
    return new Promise<void>((resolve) => {
      image.toBlob((blob) => {
        (window as unknown as ViewerWindow).viewerStream.frame(blob!);
        resolve();
      });
    });
  }, fill);
}

async function canvasState(): Promise<{ width: number; pixel: number[] }> {
  return page.locator("canvas").evaluate((canvas) => {
    const frame = canvas as HTMLCanvasElement;
    return {
      width: frame.width,
      pixel: [...frame.getContext("2d")!.getImageData(0, 0, 1, 1).data.slice(0, 4)],
    };
  });
}

function waitForCanvasPixel(color: [number, number, number]) {
  return page.waitForFunction(([red, green, blue]) => {
    const canvas = document.querySelector("canvas");
    if (!canvas) return false;
    const pixel = canvas.getContext("2d")!.getImageData(0, 0, 1, 1).data;
    return pixel[0] === red && pixel[1] === green && pixel[2] === blue;
  }, color);
}

async function waitForDecodesIdle() {
  await page.waitForFunction(() => {
    const decode = (window as unknown as ViewerWindow).viewerDecode;
    return decode.stats().inFlight === 0;
  });
}

void test("joining and shrinking a viewer leaves the remote viewport untouched", async () => {
  await page.setViewportSize({ width: 420, height: 300 });
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(resolve)));
  assert.deepEqual(await commands(), []);
});

void test("pointer taps map to frame coordinates and clamp outside the canvas", async () => {
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
  assert.equal(
    await page
      .getByLabel("Input for Example")
      .evaluate((element) => element === document.activeElement),
    true,
  );
  await page.keyboard.press("Tab");
  assert.equal(
    await page
      .getByLabel("Input for Example")
      .evaluate((element) => element === document.activeElement),
    false,
  );
  assert.deepEqual(await commands(), []);
});

void test("typing, paste, editing and composition send each input once", async () => {
  await page.getByRole("application").focus();
  await page.keyboard.type("Hi");
  await page.keyboard.press("Backspace");
  const extendSelection = "Shift+ArrowLeft";
  await page.keyboard.press(extendSelection);
  await page.keyboard.press("Control+a");
  await page.keyboard.insertText(" pasted 🐱");
  assert.deepEqual(await commands(), [
    { type: "text", text: "H" },
    { type: "text", text: "i" },
    { type: "key", key: "Backspace" },
    { type: "key", key: extendSelection },
    { type: "key", key: "Control+a" },
    { type: "text", text: " pasted 🐱" },
  ]);
  await page.getByLabel("Input for Example").evaluate((input: HTMLTextAreaElement) => {
    input.dispatchEvent(new CompositionEvent("compositionstart", { bubbles: true }));
    input.value = "に";
    input.dispatchEvent(new InputEvent("input", { bubbles: true, isComposing: true, data: "に" }));
    input.dispatchEvent(new CompositionEvent("compositionend", { bubbles: true, data: "に" }));
    input.dispatchEvent(new InputEvent("input", { bubbles: true, data: "に" }));
  });
  assert.deepEqual(await commands(), [{ type: "text", text: "に" }]);
  assert.equal(await page.getByRole("button", { name: "Send text" }).count(), 0);
});

void test("input bridge is read-only while disconnected and discards input", async () => {
  await commands();
  const input = page.getByLabel("Input for Example");
  assert.equal(
    await input.evaluate((element) => element.classList.contains("browser-page-input-bridge")),
    true,
  );
  assert.deepEqual(
    await input.evaluate((element: HTMLTextAreaElement) => ({
      spellcheck: element.spellcheck,
      autocapitalize: element.getAttribute("autocapitalize"),
      autocomplete: element.getAttribute("autocomplete"),
    })),
    { spellcheck: false, autocapitalize: "off", autocomplete: "off" },
  );
  await page.getByRole("application").focus();
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerStream.close());
  assert.equal(await page.locator(".browser-page-status").textContent(), "Connection lost.");
  assert.equal(await page.locator(".browser-page-status").isVisible(), true);
  assert.equal(await input.evaluate((element: HTMLTextAreaElement) => element.readOnly), true);
  assert.equal(await input.evaluate((element) => element === document.activeElement), true);
  assert.equal(await input.evaluate((element) => getComputedStyle(element).pointerEvents), "none");
  await page.keyboard.type("silent");
  await page.keyboard.insertText("paste");
  await input.evaluate((element: HTMLTextAreaElement) => {
    element.value = "stale";
    element.dispatchEvent(new InputEvent("input", { bubbles: true, data: "stale" }));
  });
  assert.equal(await input.inputValue(), "");
  assert.deepEqual(await commands(), []);

  await page.evaluate(() => (window as unknown as ViewerWindow).viewerStream.open());
  assert.equal(
    await page.locator(".browser-page-status").textContent(),
    "Waiting for first frame.",
  );
  assert.equal(await input.evaluate((element: HTMLTextAreaElement) => element.readOnly), false);
  assert.equal(await input.evaluate((element) => element === document.activeElement), true);
  assert.equal(await input.inputValue(), "");
  assert.deepEqual(await commands(), []);
  await page.keyboard.insertText("current");
  assert.deepEqual(await commands(), [{ type: "text", text: "current" }]);
});

void test("CDP IME composition sends committed text once", async () => {
  await commands();
  await page.getByLabel("Input for Example").focus();
  const cdp = await page.context().newCDPSession(page);
  try {
    await cdp.send("Input.imeSetComposition", {
      text: "に",
      selectionStart: 1,
      selectionEnd: 1,
    });
    await cdp.send("Input.insertText", { text: "に" });
  } finally {
    await cdp.detach();
  }
  assert.deepEqual(await commands(), [{ type: "text", text: "に" }]);
});

// Frame-pipeline regressions; these run after the pointer tests so the canvas
// keeps its setup size until a real frame is painted.

void test("status stays visible until the first frame is actually drawn", async () => {
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.reset());
  const status = page.locator(".browser-page-status");
  assert.equal(await status.textContent(), "Waiting for first frame.");
  await page.evaluate(() =>
    (window as unknown as ViewerWindow).viewerStream.frame(
      new Blob([new Uint8Array([21, 7, 4, 0])], { type: "image/jpeg" }),
    ),
  );
  // Undecodable frames are dropped; the page is still waiting.
  assert.equal(await status.textContent(), "Waiting for first frame.");
  await deliverFrame("rgb(32, 96, 160)");
  await waitForCanvasPixel([32, 96, 160]);
  assert.equal(await status.count(), 0);
  assert.deepEqual(await decodeStats(), { decodeRequests: 2, inFlight: 0, peakConcurrent: 1 });
});

void test("busy streams decode one frame at a time and keep only the latest", async () => {
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.reset());
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.stallNext());
  await deliverFrame("rgb(200, 32, 32)");
  await deliverFrame("rgb(32, 200, 32)");
  await deliverFrame("rgb(64, 32, 255)");
  // The stalled decode is the only one in flight; later frames just replace
  // the queued one instead of piling up.
  assert.deepEqual(await decodeStats(), { decodeRequests: 1, inFlight: 1, peakConcurrent: 1 });
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.release());
  await waitForCanvasPixel([64, 32, 255]);
  assert.deepEqual(await decodeStats(), { decodeRequests: 2, inFlight: 0, peakConcurrent: 1 });
});

void test("in-flight frames from the previous page are discarded on page switches", async () => {
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.reset());
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.stallNext());
  await deliverFrame("rgb(200, 32, 32)");
  await page.evaluate(() =>
    (window as unknown as ViewerWindow).viewerShowPage({
      id: "page-2",
      ownerId: "owner",
      title: "Second",
      url: "https://example.org",
      active: true,
    }),
  );
  await page.locator(".browser-page-status").waitFor();
  assert.equal(
    await page.locator(".browser-page-status").textContent(),
    "Waiting for first frame.",
  );
  await page
    .locator("canvas")
    .waitForFunction((canvas) => (canvas as HTMLCanvasElement).width === 1);
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.release());
  await waitForDecodesIdle();
  // The stalled frame belonged to the old page and is never painted.
  assert.deepEqual(await canvasState(), { width: 1, pixel: [0, 0, 0, 0] });
  await deliverFrame("rgb(32, 160, 96)");
  await waitForCanvasPixel([32, 160, 96]);
});

void test("in-flight frames from a dropped connection are discarded on reconnect", async () => {
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.reset());
  const status = page.locator(".browser-page-status");
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.stallNext());
  await deliverFrame("rgb(200, 32, 32)");
  await deliverFrame("rgb(200, 160, 32)");
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerStream.close());
  assert.equal(await status.textContent(), "Connection lost.");
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerStream.open());
  assert.equal(await status.textContent(), "Waiting for first frame.");
  // Reconnecting before decode resolves must not allow an old or queued frame
  // to paint as the first frame of the new connection.
  await page.evaluate(() => (window as unknown as ViewerWindow).viewerDecode.release());
  await waitForDecodesIdle();
  assert.deepEqual(await canvasState(), { width: 4, pixel: [32, 160, 96, 255] });
  assert.equal(await status.textContent(), "Waiting for first frame.");
  assert.deepEqual(await decodeStats(), { decodeRequests: 1, inFlight: 0, peakConcurrent: 1 });
  await deliverFrame("rgb(64, 32, 255)");
  await waitForCanvasPixel([64, 32, 255]);
});
