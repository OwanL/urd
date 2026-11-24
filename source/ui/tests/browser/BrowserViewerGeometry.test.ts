import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";

let server: ViteDevServer;
let browser: Browser;

before(async () => {
  server = await createServer({ configFile: false, server: { port: 0 }, appType: "custom" });
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/geometry-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><meta name="viewport" content="width=device-width, initial-scale=1"><script type="module" src="/tests/browser/BrowserViewerGeometryFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser?.close();
  await server?.close();
});

async function openViewer(width: number, height: number): Promise<Page> {
  const page = await browser.newPage({ viewport: { width, height } });
  await page.goto(`${server.resolvedUrls!.local[0]}geometry-test`);
  await page.getByRole("button", { name: "Browser pages (1)" }).click();
  await page.waitForFunction(() => document.querySelector("canvas")?.width === 1280);
  return page;
}

async function geometry(page: Page) {
  return page.evaluate(() => {
    const rect = (selector: string) => {
      const bounds = document.querySelector(selector)!.getBoundingClientRect();
      return { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height };
    };
    return {
      panel: rect(".browser-viewer-panel"),
      surface: rect(".browser-page-surface"),
      canvas: rect("canvas"),
    };
  });
}

function assertCanvasFits({ surface, canvas }: Awaited<ReturnType<typeof geometry>>) {
  assert.ok(
    canvas.width > 100 && canvas.height > 50,
    `canvas is usable: ${JSON.stringify(canvas)}`,
  );
  assert.ok(
    canvas.width <= surface.width + 1 && canvas.height <= surface.height + 1,
    `canvas fits surface: ${JSON.stringify({ surface, canvas })}`,
  );
  assert.ok(
    Math.abs(canvas.width / canvas.height - 1280 / 720) < 0.02,
    `canvas preserves frame aspect ratio: ${JSON.stringify(canvas)}`,
  );
  const scale = Math.min(1, surface.width / 1280, surface.height / 720);
  assert.ok(
    Math.abs(canvas.width - 1280 * scale) < 1 && Math.abs(canvas.height - 720 * scale) < 1,
    `canvas uses available surface: ${JSON.stringify({ surface, canvas })}`,
  );
}

void test("docked viewer fills its allotted height and restores after maximize", async () => {
  const page = await openViewer(1200, 800);
  try {
    const docked = await geometry(page);
    assert.ok(
      Math.abs(docked.panel.height - 384) < 1,
      `docked height: ${JSON.stringify(docked.panel)}`,
    );
    assertCanvasFits(docked);
    await page.getByRole("button", { name: "Maximize viewer" }).click();
    const maximized = await geometry(page);
    assert.deepEqual(maximized.panel, { x: 0, y: 0, width: 1200, height: 800 });
    assertCanvasFits(maximized);
    await page.getByRole("button", { name: "Restore viewer size" }).click();
    const restored = await geometry(page);
    assert.ok(
      Math.abs(restored.panel.height - docked.panel.height) < 1,
      `restored height: ${JSON.stringify(restored.panel)}`,
    );
    assertCanvasFits(restored);
  } finally {
    await page.close();
  }
});

void test("floating viewer stays in view after viewport narrows and after maximize restore", async () => {
  const page = await openViewer(1200, 800);
  try {
    await page.getByRole("button", { name: "Float viewer" }).click();
    const header = page.locator(".browser-viewer-header");
    const bounds = (await header.boundingBox())!;
    await page.mouse.move(bounds.x + 2, bounds.y + 2);
    await page.mouse.down();
    await page.mouse.move(1190, 790);
    await page.mouse.up();
    await page.setViewportSize({ width: 360, height: 600 });
    await page.waitForFunction(() => {
      const bounds = document.querySelector(".browser-viewer-panel")!.getBoundingClientRect();
      return bounds.right <= innerWidth && bounds.bottom <= innerHeight;
    });
    const floating = await geometry(page);
    assert.ok(
      floating.panel.x >= 0 && floating.panel.x + floating.panel.width <= 360,
      JSON.stringify(floating),
    );
    assertCanvasFits(floating);
    await page.getByRole("button", { name: "Maximize viewer" }).click();
    await page.getByRole("button", { name: "Restore viewer size" }).click();
    const restored = await geometry(page);
    assert.ok(
      restored.panel.x >= 0 && restored.panel.x + restored.panel.width <= 360,
      JSON.stringify(restored),
    );
    assertCanvasFits(restored);
    await page.setViewportSize({ width: 1200, height: 800 });
    const resizedHeader = (await header.boundingBox())!;
    await page.mouse.move(resizedHeader.x + 2, resizedHeader.y + 2);
    await page.mouse.down();
    await page.mouse.move(1190, 790);
    await page.mouse.up();
    await page.getByRole("button", { name: "Maximize viewer" }).click();
    await page.setViewportSize({ width: 360, height: 600 });
    await page.getByRole("button", { name: "Restore viewer size" }).click();
    const restoredAfterResize = await geometry(page);
    assert.ok(
      restoredAfterResize.panel.x >= 0 &&
        restoredAfterResize.panel.x + restoredAfterResize.panel.width <= 360,
      JSON.stringify(restoredAfterResize),
    );
    assertCanvasFits(restoredAfterResize);
  } finally {
    await page.close();
  }
});

void test("narrow docked viewer contains the frame without distortion", async () => {
  const page = await openViewer(360, 800);
  try {
    const bounds = await geometry(page);
    assert.ok(
      Math.abs(bounds.panel.height - 384) < 1,
      `docked height: ${JSON.stringify(bounds.panel)}`,
    );
    assertCanvasFits(bounds);
  } finally {
    await page.close();
  }
});
