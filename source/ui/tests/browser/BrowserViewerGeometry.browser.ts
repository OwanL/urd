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

type Rect = { x: number; y: number; width: number; height: number };
type Edge = "left" | "right" | "top" | "bottom";

async function openViewer(width = 1200, height = 800): Promise<Page> {
  const page = await browser.newPage({ viewport: { width, height } });
  await page.goto(`${server.resolvedUrls!.local[0]}geometry-test`);
  await page.getByRole("button", { name: "Join browser" }).click();
  await page.waitForFunction(() => document.querySelector("canvas")?.width === 1280);
  return page;
}

async function rect(page: Page, selector: string): Promise<Rect> {
  const box = await page.locator(selector).boundingBox();
  assert.ok(box, `${selector} is visible`);
  return box;
}

function close(actual: number, expected: number, message: string) {
  assert.ok(Math.abs(actual - expected) <= 2, `${message}: ${actual} != ${expected}`);
}

function sameRect(actual: Rect, expected: Rect) {
  for (const key of ["x", "y", "width", "height"] as const) close(actual[key], expected[key], key);
}

async function assertCanvasFits(page: Page) {
  const surface = await rect(page, ".browser-page-surface");
  const canvas = await rect(page, "canvas");
  assert.ok(
    canvas.width > 100 && canvas.height > 50,
    `canvas is usable: ${JSON.stringify(canvas)}`,
  );
  assert.ok(
    canvas.width <= surface.width + 1 && canvas.height <= surface.height + 1,
    "canvas fits surface",
  );
  assert.ok(
    Math.abs(canvas.width / canvas.height - 1280 / 720) < 0.02,
    "canvas preserves frame aspect ratio",
  );
  const scale = Math.min(1, surface.width / 1280, surface.height / 720);
  assert.ok(
    Math.abs(canvas.width - 1280 * scale) < 1 && Math.abs(canvas.height - 720 * scale) < 1,
    "canvas uses available surface",
  );
}

async function assertChatContentsFit(page: Page) {
  const chat = await rect(page, ".chat-pane");
  for (const selector of [".transcript-area", ".composer"]) {
    const content = await rect(page, selector);
    assert.ok(
      content.x >= chat.x - 1 &&
        content.y >= chat.y - 1 &&
        content.x + content.width <= chat.x + chat.width + 1 &&
        content.y + content.height <= chat.y + chat.height + 1,
      `${selector} fits chat: ${JSON.stringify({ chat, content })}`,
    );
  }
}

async function mode(page: Page, value: string) {
  await page.waitForFunction(
    (expected) =>
      document.querySelector(".browser-viewer-panel")?.getAttribute("data-mode") === expected,
    value,
  );
}

async function drag(page: Page, selector: string, x: number, y: number) {
  const box = await rect(page, selector);
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(x, y, { steps: 10 });
  await page.mouse.up();
}

async function snap(page: Page, edge: Edge) {
  const workspace = await rect(page, ".chat-view");
  const x =
    edge === "left"
      ? workspace.x + 1
      : edge === "right"
        ? workspace.x + workspace.width - 1
        : workspace.x + workspace.width / 2;
  const y =
    edge === "top"
      ? workspace.y + 1
      : edge === "bottom"
        ? workspace.y + workspace.height - 1
        : workspace.y + workspace.height / 2;
  await drag(page, ".browser-viewer-grip", x, y);
  await mode(page, edge);
}

async function assertSplit(page: Page, edge: Edge) {
  const workspace = await rect(page, ".chat-view");
  const panel = await rect(page, ".browser-viewer-panel");
  const chat = await rect(page, ".chat-pane");
  assert.ok(chat.width > 0 && chat.height > 0, "chat remains visible");
  await assertChatContentsFit(page);
  if (edge === "left" || edge === "right") {
    close(panel.y, workspace.y, "panel top");
    close(panel.height, workspace.height, "panel height");
    close(chat.y, workspace.y, "chat top");
    close(chat.height, workspace.height, "chat height");
    close(panel.width + chat.width, workspace.width, "split width");
    close(edge === "left" ? panel.x : chat.x, workspace.x, "split left");
    close(
      edge === "left" ? panel.x + panel.width : chat.x + chat.width,
      edge === "left" ? chat.x : panel.x,
      "split seam",
    );
  } else {
    close(panel.x, workspace.x, "panel left");
    close(panel.width, workspace.width, "panel width");
    close(chat.x, workspace.x, "chat left");
    close(chat.width, workspace.width, "chat width");
    close(panel.height + chat.height, workspace.height, "split height");
    close(edge === "top" ? panel.y : chat.y, workspace.y, "split top");
    close(
      edge === "top" ? panel.y + panel.height : chat.y + chat.height,
      edge === "top" ? chat.y : panel.y,
      "split seam",
    );
  }
}

for (const edge of ["left", "right", "top", "bottom"] as const) {
  void test(`dragging to ${edge} snaps into a true chat split`, async () => {
    const page = await openViewer();
    try {
      await snap(page, edge);
      await assertSplit(page, edge);
    } finally {
      await page.close();
    }
  });
}

void test("dragging a docked header undocks with the previous floating size", async () => {
  const page = await openViewer();
  try {
    const corner = await rect(page, '.browser-resize-handle[data-edge="se"]');
    await drag(page, '.browser-resize-handle[data-edge="se"]', corner.x - 80, corner.y - 60);
    const floating = await rect(page, ".browser-viewer-panel");
    await snap(page, "left");
    const workspace = await rect(page, ".chat-view");
    const grip = await rect(page, ".browser-viewer-grip");
    await page.mouse.move(grip.x + grip.width / 2, grip.y + grip.height / 2);
    await page.mouse.down();
    await page.mouse.move(workspace.x + workspace.width / 2, workspace.y + 100, { steps: 10 });
    await mode(page, "floating");
    const undocked = await rect(page, ".browser-viewer-panel");
    close(undocked.width, floating.width, "floating width during drag");
    close(undocked.height, floating.height, "floating height during drag");
    await page.mouse.up();
    await page.getByLabel("Window layout").selectOption("maximized");
    await page.getByLabel("Window layout").selectOption("floating");
    sameRect(await rect(page, ".browser-viewer-panel"), undocked);
  } finally {
    await page.close();
  }
});

void test("pulling a full-width maximized viewer away leaves a visible gap during the gesture", async () => {
  const page = await openViewer(580, 800);
  try {
    const workspace = await rect(page, ".chat-view");
    const floating = await rect(page, ".browser-viewer-panel");
    close(floating.width, workspace.width, "initial floating width");
    await page.getByLabel("Window layout").selectOption("maximized");
    const grip = await rect(page, ".browser-viewer-grip");
    const startX = grip.x + grip.width / 2;
    const startY = grip.y + grip.height / 2;
    const x = workspace.x + workspace.width / 2;
    const y = startY + 90;
    await page.mouse.move(startX, startY);
    await page.mouse.down();
    await page.mouse.move(x, y, { steps: 10 });
    await mode(page, "floating");
    const pulled = await rect(page, ".browser-viewer-panel");
    assert.ok(pulled.width < workspace.width - 20, "pulled panel is narrower");
    assert.ok(
      pulled.x > workspace.x && pulled.x + pulled.width < workspace.x + workspace.width,
      "horizontal gap",
    );
    assert.ok(
      pulled.y > workspace.y && pulled.y + pulled.height < workspace.y + workspace.height,
      "vertical gap",
    );
    assert.ok(
      Math.abs((x - pulled.x) / pulled.width - (startX - workspace.x) / workspace.width) < 0.02,
      "horizontal grab point stays anchored",
    );
    await page.mouse.up();
  } finally {
    await page.close();
  }
});

void test("a short pull from a docked top edge does not immediately snap back", async () => {
  const page = await openViewer();
  try {
    await page.getByLabel("Window layout").selectOption("top");
    const grip = await rect(page, ".browser-viewer-grip");
    const x = grip.x + grip.width / 2;
    const y = grip.y + grip.height / 2;
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.move(x + 8, y);
    await mode(page, "floating");
    await page.mouse.up();
    await mode(page, "floating");
  } finally {
    await page.close();
  }
});

void test("a docked header can drag along the workspace edge into another split", async () => {
  const page = await openViewer();
  try {
    await snap(page, "right");
    await snap(page, "top");
    await assertSplit(page, "top");
  } finally {
    await page.close();
  }
});

void test("corner and side handles resize the floating panel", async () => {
  const page = await openViewer();
  try {
    const before = await rect(page, ".browser-viewer-panel");
    const corner = await rect(page, '.browser-resize-handle[data-edge="se"]');
    await drag(
      page,
      '.browser-resize-handle[data-edge="se"]',
      corner.x + corner.width / 2 - 100,
      corner.y + corner.height / 2 - 80,
    );
    const afterCorner = await rect(page, ".browser-viewer-panel");
    assert.ok(
      afterCorner.width < before.width - 20 && afterCorner.height < before.height - 20,
      "corner changes both dimensions",
    );
    const side = await rect(page, '.browser-resize-handle[data-edge="e"]');
    await drag(
      page,
      '.browser-resize-handle[data-edge="e"]',
      side.x + side.width / 2 + 50,
      side.y + side.height / 2,
    );
    const afterSide = await rect(page, ".browser-viewer-panel");
    assert.ok(afterSide.width > afterCorner.width + 20, "side changes width");
    close(afterSide.height, afterCorner.height, "side preserves height");
  } finally {
    await page.close();
  }
});

void test("floating panel stays within the workspace when the viewport shrinks", async () => {
  const page = await openViewer();
  try {
    await drag(page, ".browser-viewer-grip", 1000, 650);
    await page.setViewportSize({ width: 360, height: 600 });
    await page.waitForFunction(() => {
      const panel = document.querySelector(".browser-viewer-panel")!.getBoundingClientRect();
      const workspace = document.querySelector(".chat-view")!.getBoundingClientRect();
      return (
        panel.left >= workspace.left - 1 &&
        panel.top >= workspace.top - 1 &&
        panel.right <= workspace.right + 1 &&
        panel.bottom <= workspace.bottom + 1
      );
    });
    await assertCanvasFits(page);
    await assertChatContentsFit(page);
    const floating = await rect(page, ".browser-viewer-panel");
    await page.getByLabel("Window layout").selectOption("maximized");
    await mode(page, "maximized");
    await assertCanvasFits(page);
    await page.getByLabel("Window layout").selectOption("floating");
    await mode(page, "floating");
    sameRect(await rect(page, ".browser-viewer-panel"), floating);
    await assertCanvasFits(page);
    await assertChatContentsFit(page);
    await page.setViewportSize({ width: 1200, height: 800 });
    await drag(page, ".browser-viewer-grip", 1000, 650);
    await page.getByLabel("Window layout").selectOption("maximized");
    await mode(page, "maximized");
    await page.setViewportSize({ width: 360, height: 600 });
    await page.getByLabel("Window layout").selectOption("floating");
    await mode(page, "floating");
    const restored = await rect(page, ".browser-viewer-panel");
    const workspace = await rect(page, ".chat-view");
    assert.ok(
      restored.x >= workspace.x && restored.x + restored.width <= workspace.x + workspace.width + 1,
      "restored panel fits narrow workspace",
    );
    await assertCanvasFits(page);
    await assertChatContentsFit(page);
  } finally {
    await page.close();
  }
});

void test("minimize removes the panel and reopening restores its floating rectangle", async () => {
  const page = await openViewer();
  try {
    const before = await rect(page, ".browser-viewer-panel");
    const minimize = page.getByRole("button", { name: "Minimize viewer" });
    const bounds = (await minimize.boundingBox())!;
    // This point also falls inside the north-east corner's expanded grab area.
    await page.mouse.click(bounds.x + bounds.width - 2, bounds.y + 2);
    await page.locator(".browser-viewer-panel").waitFor({ state: "detached" });
    await page.getByRole("button", { name: "Join browser" }).click();
    await mode(page, "floating");
    sameRect(await rect(page, ".browser-viewer-panel"), before);
  } finally {
    await page.close();
  }
});

void test("docked split resizes by pointer and keyboard and survives minimize", async () => {
  const page = await openViewer();
  try {
    await snap(page, "left");
    const before = await rect(page, ".browser-viewer-panel");
    const divider = await rect(page, ".browser-divider");
    await drag(
      page,
      ".browser-divider",
      divider.x + divider.width / 2 + 100,
      divider.y + divider.height / 2,
    );
    const resized = await rect(page, ".browser-viewer-panel");
    assert.ok(resized.width > before.width + 50, "pointer changes split");
    await assertSplit(page, "left");
    await page.getByRole("separator", { name: "Browser split" }).focus();
    await page.keyboard.press("ArrowRight");
    const keyboard = await rect(page, ".browser-viewer-panel");
    assert.ok(keyboard.width > resized.width, "arrow changes split");
    await assertSplit(page, "left");
    await page.getByRole("button", { name: "Minimize viewer" }).click();
    await page.locator(".browser-viewer-panel").waitFor({ state: "detached" });
    await page.getByRole("button", { name: "Join browser" }).click();
    await mode(page, "left");
    sameRect(await rect(page, ".browser-viewer-panel"), keyboard);
    await assertSplit(page, "left");
  } finally {
    await page.close();
  }
});

void test("frame fits in narrow docked, maximized and restored layouts", async () => {
  const page = await openViewer(360, 800);
  try {
    await page.getByLabel("Window layout").selectOption("bottom");
    await mode(page, "bottom");
    await assertSplit(page, "bottom");
    const docked = await rect(page, ".browser-viewer-panel");
    await assertCanvasFits(page);
    await page.getByLabel("Window layout").selectOption("maximized");
    await mode(page, "maximized");
    sameRect(await rect(page, ".browser-viewer-panel"), await rect(page, ".chat-view"));
    await assertCanvasFits(page);
    await page.getByLabel("Window layout").selectOption("bottom");
    await mode(page, "bottom");
    sameRect(await rect(page, ".browser-viewer-panel"), docked);
    await assertCanvasFits(page);
    await assertSplit(page, "bottom");
  } finally {
    await page.close();
  }
});

void test("keyboard layout control changes layout without a pointer gesture", async () => {
  const page = await openViewer();
  try {
    const layout = page.getByLabel("Window layout");
    await layout.focus();
    await page.keyboard.press("Home");
    // Select by typing the native option label, as a keyboard user can.
    await page.keyboard.type("right");
    await page.keyboard.press("Enter");
    await mode(page, "right");
    await assertSplit(page, "right");
  } finally {
    await page.close();
  }
});
