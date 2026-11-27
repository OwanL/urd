import assert from "node:assert/strict";
import test from "node:test";
import { chromium } from "playwright";
import { createServer } from "vite";

void test("browser chimes work in the background without keeping chat subscriptions", async (t) => {
  const server = await createServer({ server: { port: 0 }, appType: "custom" });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/attention-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/AttentionSoundsFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  await page.goto(`${server.resolvedUrls!.local[0]}attention-test`);
  await page.waitForFunction(() => window.attentionFixture?.registrations.length === 1);
  const send = (kind: string) =>
    page.evaluate((kind) => window.attentionFixture.send("attention", { kind }), kind);
  const flushEvents = async (marker: string) => {
    await page.evaluate((detail) => {
      window.attentionFixture.send("browser-provisioning", { state: "downloading", detail });
    }, marker);
    await page.waitForFunction(
      (text) => document.querySelector("output")?.textContent === text,
      marker,
    );
  };
  await send("question"); // No user gesture yet.
  await flushEvents("before gesture");
  assert.deepEqual(await page.evaluate(() => window.attentionFixture.frequencies), []);
  await page.getByRole("button", { name: "Use Urd" }).click();
  await page.evaluate(() => window.attentionFixture.setHidden(true));
  await page.waitForFunction(() => window.attentionFixture.registrations.length === 2);
  assert.deepEqual(await page.evaluate(() => window.attentionFixture.registrations[1]), {
    open: [],
    watched: [],
  });
  await send("question");
  await send("completion");
  await send("unknown");
  await flushEvents("after chimes");
  const notes = await page.evaluate(() => window.attentionFixture.frequencies);
  assert.equal(notes.length, 4);
  assert.ok(notes[0]! > notes[1]!, "the question chime descends");
  assert.ok(notes[2]! < notes[3]!, "the completion chime rises");

  await page.evaluate(() => window.attentionFixture.setHidden(false));
  await page.waitForFunction(() => window.attentionFixture.registrations.length === 3);
  assert.equal(await page.evaluate(() => window.attentionFixture.streamCount), 1);
  const sounds = page.getByRole("checkbox", { name: "Sounds" });
  await sounds.uncheck();
  await send("question");
  await flushEvents("muted");
  assert.deepEqual(await page.evaluate(() => window.attentionFixture.frequencies), notes);
  await page.reload();
  await sounds.waitFor();
  assert.equal(await sounds.isChecked(), false);
});
