import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright";
import { createServer, type ViteDevServer } from "vite";
import type { UserQuestion } from "../../sessions/inputs/UserQuestion.ts";

let server: ViteDevServer;
let browser: Browser;
let page: Page;
const short: UserQuestion = {
  id: "short",
  askingSessionId: "fixture",
  question: "Short question?",
  context: null,
  answers: ["Yes", "No"],
  sourceTitle: null,
  background: false,
};
const long: UserQuestion = {
  ...short,
  id: "long",
  question: "Long question?",
  context: "A longer question with additional context. ".repeat(12),
};

before(async () => {
  server = await createServer({ configFile: false, server: { port: 0 }, appType: "custom" });
  server.middlewares.use((request, response, next) => {
    if (request.url !== "/questions-test") return next();
    response.setHeader("Content-Type", "text/html");
    response.end(
      '<!doctype html><script type="module" src="/tests/browser/UserQuestionsFixture.tsx"></script>',
    );
  });
  await server.listen();
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage();
  await page.goto(`${server.resolvedUrls!.local[0]}questions-test`);
  await page.waitForFunction(() => "showQuestions" in window);
  await page.locator("#history").evaluate((element) => {
    element.scrollTop = 500;
  });
});

after(async () => {
  await browser?.close();
  await server?.close();
});

async function show(questions: UserQuestion[]) {
  await page.evaluate((value) => {
    (window as unknown as { showQuestions: (questions: UserQuestion[]) => void }).showQuestions(
      value,
    );
  }, questions);
}

// Sample the space occupied by the panel, including its margins, without relying on its markup.
async function sample(action: () => Promise<void>, duration = 650) {
  await page.evaluate((duration) => {
    const history = document.querySelector("#history")!;
    const composer = document.querySelector("#composer")!;
    const row = document.querySelector("#row-22")!;
    const samples: { gap: number; composer: number; anchor: number }[] = [];
    (window as unknown as { samples: typeof samples }).samples = samples;
    const start = performance.now();
    function frame(now: number) {
      const h = history.getBoundingClientRect();
      const c = composer.getBoundingClientRect();
      samples.push({
        gap: c.top - h.bottom,
        composer: c.top,
        anchor: row.getBoundingClientRect().top,
      });
      if (now - start < duration) requestAnimationFrame(frame);
      else (window as unknown as { sampled: boolean }).sampled = true;
    }
    (window as unknown as { sampled: boolean }).sampled = false;
    requestAnimationFrame(frame);
  }, duration);
  await action();
  await page.waitForFunction(() => (window as unknown as { sampled: boolean }).sampled);
  return page.evaluate(
    () =>
      (window as unknown as { samples: { gap: number; composer: number; anchor: number }[] })
        .samples,
  );
}

void test("questions animate without moving composer or a scrolled-up history anchor", async () => {
  const opening = await sample(() => show([short, long]));
  const swap = await sample(() => page.getByRole("button", { name: "Next question" }).click());
  await page.getByText("Long question?").waitFor();
  await page.getByRole("button", { name: "Other answer" }).click();
  await page.getByRole("textbox", { name: "Custom answer" }).fill("draft");
  assert.equal(await page.getByRole("textbox", { name: "Custom answer" }).inputValue(), "draft");
  const closing = await sample(() => show([]));
  assert.equal(await page.getByRole("region", { name: "Pending questions" }).count(), 0);
  const reopened = await sample(async () => {
    await show([short]);
    await page.getByText("Short question?").waitFor();
    await show([]);
    await page.evaluate(
      () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    );
    await show([short]);
  });
  await page.getByRole("button", { name: "Yes" }).click();
  await page.waitForFunction(() => !document.querySelector(".user-question-text"));
  await page.emulateMedia({ reducedMotion: "reduce" });
  const reduced = await sample(() => show([short]));
  const reducedClose = await sample(() => show([]));
  const all = [...opening, ...swap, ...closing, ...reopened, ...reduced, ...reducedClose];
  assert.ok(
    all.every((point) => Math.abs(point.composer - all[0]!.composer) < 1),
    "composer moved",
  );
  assert.ok(
    all.every((point) => Math.abs(point.anchor - all[0]!.anchor) < 1),
    "history anchor moved",
  );
  assert.ok(Math.abs(closing.at(-1)!.gap) < 1, "closed panel left a gap");
  assert.ok(Math.abs(reducedClose.at(-1)!.gap) < 1, "reduced-motion close left a gap");
  assert.ok(swap.at(-1)!.gap > opening.at(-1)!.gap, "long question did not expand");
  const hasIntermediate = (values: typeof opening, end: number) =>
    values.some(({ gap }) => gap > 1 && gap < end - 1);
  assert.ok(hasIntermediate(opening, opening.at(-1)!.gap), "opening had no intermediate height");
  assert.ok(
    swap.some(({ gap }) => gap > opening.at(-1)!.gap + 1 && gap < swap.at(-1)!.gap - 1),
    "switching questions snapped to the new height",
  );
  assert.ok(hasIntermediate(closing, closing[0]!.gap), "closing had no intermediate height");
  assert.ok(
    reduced.slice(3).every(({ gap }) => Math.abs(gap - reduced.at(-1)!.gap) < 1),
    "reduced-motion opening animated",
  );
  assert.ok(
    reducedClose.slice(3).every(({ gap }) => Math.abs(gap) < 1),
    "reduced-motion closing animated",
  );
});
