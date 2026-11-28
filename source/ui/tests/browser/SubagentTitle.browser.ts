import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import { chromium } from "playwright";
import { createServer } from "vite";

const subagentCall = (values: { title: string; titlePending: boolean; live: boolean }) => ({
  id: "child",
  parentId: "parent",
  title: values.title,
  titlePending: values.titlePending,
  live: values.live,
  modelId: "fixture-model",
  startedAt: values.live ? new Date(Date.now() - 12_000).toISOString() : null,
  elapsedMilliseconds: values.live ? null : 12_345,
  durationIncomplete: false,
  cost: {
    own: { usd: 0.1, partial: false },
    descendants: { usd: 0.2, partial: false },
    total: { usd: 0.3, partial: false },
    hasDescendants: true,
  },
});

async function setup(t: TestContext, width = 800) {
  const server = await createServer({ server: { port: 0 }, appType: "custom" });
  t.after(() => server.close());
  server.middlewares.use((request, response, next) => {
    if (request.url?.split("?", 1)[0] !== "/subagent-title-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/SubagentTitleFixture.tsx"></script>',
    );
  });
  await server.listen();
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage({ viewport: { width, height: 480 } });
  await page.clock.install();
  return { page, url: `${server.resolvedUrls!.local[0]}subagent-title-test` };
}

void test("a visible subagent row retries a pending title and polls live metadata until settled", async (t) => {
  const { page, url } = await setup(t);
  let title = "Subagent";
  let titlePending = true;
  let live = true;
  let reads = 0;
  await page.route("**/api/sessions/parent/subagents/by-call/request/call", async (route) => {
    reads++;
    await route.fulfill({ json: subagentCall({ title, titlePending, live }) });
  });
  await page.goto(url);

  await page.getByRole("button", { name: "Subagent", exact: true }).focus({ timeout: 5_000 });
  await page.locator(".subagent-running").waitFor();
  const nextRead = () =>
    page.waitForResponse("**/api/sessions/parent/subagents/by-call/request/call");
  await Promise.all([nextRead(), page.clock.runFor(1_000)]);
  assert.ok(reads > 1, "pending title is retried");

  title = "Fixture child";
  titlePending = false;
  const renamed = page.getByRole("button", { name: title, exact: true });
  await renamed.waitFor({ timeout: 5_000 });
  assert.equal(await renamed.evaluate((button) => document.activeElement === button), true);

  await Promise.all([nextRead(), page.clock.runFor(3_000)]);
  live = false;
  await page.locator(".subagent-running").waitFor({ state: "hidden", timeout: 5_000 });
  assert.equal(await page.locator(".subagent-elapsed").innerText(), "0:12");
  const settledReads = reads;
  await page.clock.runFor(5_000);
  assert.equal(reads, settledReads, "polling stops after the call is no longer live");
  assert.equal(await page.locator(".subagent-elapsed").innerText(), "0:12");
});

void test("a narrow subagent row keeps title, model, elapsed time, and cost on one line with a dismissible cost breakdown", async (t) => {
  const { page, url } = await setup(t, 245);
  await page.route("**/api/sessions/parent/subagents/by-call/request/call", async (route) => {
    await route.fulfill({
      json: subagentCall({
        title: "A long subagent title to truncate on a narrow pane",
        titlePending: false,
        live: false,
      }),
    });
  });
  await page.goto(url);

  const line = page.locator(".subagent-line");
  await page
    .getByRole("button", { name: "A long subagent title to truncate on a narrow pane" })
    .waitFor({ timeout: 5_000 });
  for (const selector of [
    ".subagent-title",
    ".subagent-model",
    ".subagent-elapsed",
    ".subagent-cost",
  ]) {
    await page.locator(selector).waitFor({ timeout: 5_000 });
  }
  const aligned = await Promise.all(
    [".subagent-title", ".subagent-model", ".subagent-elapsed", ".subagent-cost"].map((selector) =>
      page.locator(selector).evaluate((element) => {
        const line = element.closest(".subagent-line")!;
        const bounds = element.getBoundingClientRect();
        const lineBounds = line.getBoundingClientRect();
        return { center: (bounds.top + bounds.bottom) / 2, lineHeight: lineBounds.height };
      }),
    ),
  );
  assert.ok(
    Math.max(...aligned.map((bounds) => bounds.center)) -
      Math.min(...aligned.map((bounds) => bounds.center)) <=
      8,
    "metadata is vertically aligned on one line",
  );
  assert.ok(
    aligned.every((bounds) => bounds.lineHeight <= 36),
    "the narrow row remains one line high",
  );
  assert.ok(
    await line.evaluate((element) => element.scrollWidth <= element.clientWidth),
    "the aligned row fits the narrow viewport without horizontal overflow",
  );
  assert.equal(
    await page.locator(".subagent-report:not(:empty)").count(),
    0,
    "no inline report is rendered",
  );

  const cost = page.locator(".subagent-cost").getByRole("button");
  const name = await cost.getAttribute("aria-label");
  assert.ok(name?.includes("$0.30"), `cost control names its formatted amount: ${name}`);
  assert.match(name ?? "", /descendant/i, "cost control name indicates descendant costs");
  await cost.click();
  const breakdown = page.getByRole("dialog", { name: "Subagent cost breakdown" });
  await breakdown.waitFor({ timeout: 5_000 });
  for (const label of ["This subagent", "Nested subagents", "Total"]) {
    assert.ok(
      await breakdown.getByText(label, { exact: true }).count(),
      `missing ${label} cost row`,
    );
  }
  await page.keyboard.press("Escape");
  await breakdown.waitFor({ state: "hidden", timeout: 5_000 });
  assert.equal(await page.locator(".subagent-model").innerText(), "Sonnet 4.6");
  await page.setViewportSize({ width: 800, height: 480 });
  assert.equal(await page.locator(".subagent-model").innerText(), "Claude Sonnet 4.6");
  await page
    .getByRole("button", { name: "A long subagent title to truncate on a narrow pane" })
    .click();
  await page.locator("output").getByText("child", { exact: true }).waitFor();
});

void test("a failed subagent report is available on demand without changing the row height", async (t) => {
  const { page, url } = await setup(t, 360);
  let reports = 0;
  await page.route("**/api/sessions/parent/subagents/by-call/request/call", async (route) => {
    await route.fulfill({ status: 404 });
  });
  await page.route("**/api/sessions/parent/tool-calls/request/call?**", async (route) => {
    reports++;
    await route.fulfill({ json: { output: "No eligible model", truncated: false } });
  });
  await page.goto(`${url}?failed`);
  const line = page.locator(".subagent-line");
  const report = page.getByRole("button", { name: "Failed. Open subagent report" });
  await report.waitFor();
  const height = (await line.boundingBox())!.height;
  assert.equal(reports, 0);
  await report.focus();
  const dialog = page.getByRole("dialog", { name: "Subagent report" });
  await dialog.getByText("Failed: No eligible model", { exact: true }).waitFor();
  assert.equal(reports, 1);
  assert.equal((await line.boundingBox())!.height, height);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden" });
});
