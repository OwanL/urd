import assert from "node:assert/strict";
import test from "node:test";
import { chromium, type Page } from "playwright";
import { createServer } from "vite";

async function frames(page: Page) {
  await page.evaluate(async () => {
    for (let i = 0; i < 8; i++)
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  });
}

void test("width-only resize preserves the space between an Answer and the following reply", async (t) => {
  const server = await createServer({
    configFile: false,
    optimizeDeps: { noDiscovery: true },
    server: { port: 0 },
    appType: "custom",
  });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/transcript-resize-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/TranscriptResizeFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
  await page.goto(`${server.resolvedUrls!.local[0]}transcript-resize-test`);

  const answer = page.locator('[data-entry-rank="0"]');
  const reply = page.locator('[data-entry-rank="1"]');
  await page.getByText("Fourth answer line").waitFor();
  await page.getByText("Following assistant reply").waitFor();
  assert.equal(
    await answer.locator(".agent-exchange-text").textContent(),
    "First answer line\nSecond answer line\nThird answer line\nFourth answer line",
  );
  await frames(page);
  const beforeAnswer = await answer.boundingBox();
  const beforeReply = await reply.boundingBox();
  assert.ok(beforeAnswer && beforeReply && beforeAnswer.height > 0 && beforeReply.height > 0);
  assert.ok(
    beforeReply.y >= beforeAnswer.y + beforeAnswer.height - 1,
    "initial rows do not overlap",
  );

  await page.setViewportSize({ width: 790, height: 600 });
  await frames(page);
  const afterAnswer = await answer.boundingBox();
  const afterReply = await reply.boundingBox();
  assert.ok(afterAnswer && afterReply && afterAnswer.height > 0 && afterReply.height > 0);
  assert.equal(afterAnswer.width, beforeAnswer.width - 10, "transcript narrowed by 10px");
  assert.ok(afterAnswer.y >= 0 && afterReply.y >= 0 && afterAnswer.y + afterAnswer.height < 600);
  assert.equal(afterAnswer.height, beforeAnswer.height, "answer did not rewrap");
  assert.ok(
    afterReply.y >= afterAnswer.y + afterAnswer.height - 1,
    `reply overlaps Answer: ${JSON.stringify({ afterAnswer, afterReply })}`,
  );
});
