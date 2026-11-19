import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { setTimeout as delay } from "node:timers/promises";
import { chromium, type Browser, type Locator, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import { startTranscriptFixture, type FixtureSession } from "./TranscriptFixture.ts";

type Fixture = Awaited<ReturnType<typeof startTranscriptFixture>>;
let vite: ViteDevServer | undefined;
let browser: Browser | undefined;

before(async () => {
  try {
    vite = await createServer({
      server: {
        host: "127.0.0.1",
        port: 0,
        hmr: false,
        watch: null,
      },
    });
    await vite.listen();
    browser = await chromium.launch();
  } catch (error) {
    throw new Error(
      "Could not start the browser fixture (install pinned Chromium with npx playwright install chromium).",
      { cause: error },
    );
  }
});

after(async () => {
  await browser?.close();
  await vite?.close();
});

export function browserCase(
  name: string,
  run: (context: { page: Page; fixture: Fixture; port: Locator }) => Promise<void>,
  session: FixtureSession = "history",
) {
  void test(name, { timeout: 20_000 }, async () => {
    const address = vite!.httpServer!.address();
    assert(address && typeof address !== "string");
    const context = await browser!.newContext({
      viewport: { width: 900, height: 560 },
      reducedMotion: "reduce",
    });
    let fixture: Fixture | undefined;
    const errors: Error[] = [];
    try {
      fixture = await startTranscriptFixture();
      const page = await context.newPage();
      page.on("pageerror", (error) => errors.push(error));
      page.setDefaultTimeout(4_000);
      await page.goto(
        `http://127.0.0.1:${address.port}/tests/support/browser/harness.html?session=${session}&api=${encodeURIComponent(fixture.origin.origin)}`,
      );
      await run({ page, fixture, port: page.getByRole("region", { name: "Conversation" }) });
      assert.deepEqual(
        errors.map((error) => error.message),
        [],
      );
    } finally {
      try {
        await context.close();
      } finally {
        await fixture?.close();
      }
    }
  });
}

export async function until(description: string, check: () => Promise<boolean>) {
  const deadline = performance.now() + 4_000;
  let matched = await check();
  while (!matched && performance.now() < deadline) {
    await delay(25);
    matched = await check();
  }
  assert(matched, description);
}

export async function visibleOffset(item: Locator, port: Locator): Promise<number | null> {
  const bounds = await port.boundingBox();
  if (!bounds || (await item.count()) !== 1) return null;
  const box = await item.boundingBox();
  if (!box || box.y + box.height <= bounds.y || box.y >= bounds.y + bounds.height) return null;
  return box.y - bounds.y;
}

export async function scrollBy(port: Locator, pixels: number) {
  await port.evaluate((element, dy) => element.scrollBy(0, dy), pixels);
}

// Require a postcondition beyond the publication frame, including its queued scroll/resize work.
export async function settle(page: Page) {
  await page.evaluate(async () => {
    for (let frame = 0; frame < 8; frame++) await new Promise(requestAnimationFrame);
  });
}
