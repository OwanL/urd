import assert from "node:assert/strict";
import test from "node:test";
import { chromium, type Page } from "playwright";
import { createServer } from "vite";
import { safeMarkdownLink } from "../markdown-rendering/MarkdownLinkPolicy.ts";
import type { MarkdownRenderSnapshot } from "../markdown-rendering/MarkdownRenderModel.ts";

declare global {
  interface Window {
    markdownRendererHarness: {
      readonly links: string[];
      readonly snapshot: MarkdownRenderSnapshot;
      append(): Promise<void>;
      duplicate(): void;
    };
  }
}

void test("standalone Markdown renderer in Chromium", async (t) => {
  const server = await createServer({
    configFile: false,
    server: { host: "127.0.0.1", port: 0 },
    logLevel: "error",
  });
  await server.listen();
  const origin = server.resolvedUrls!.local[0]!;
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 900 } });
    const errors: string[] = [];
    const unexpected: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    page.on("request", (request) => {
      if (!request.url().startsWith(origin)) unexpected.push(request.url());
    });
    await page.route("**/*", async (route) => {
      if (route.request().url().startsWith(origin)) await route.continue();
      else await route.abort();
    });
    async function open(mode = "") {
      await page.goto(`${origin}tests/MarkdownRendererHarness.html${mode ? `?mode=${mode}` : ""}`);
      await page.waitForSelector('html[data-ready="true"]');
      await page.evaluate(() => document.fonts.ready);
    }
    await open();
    await t.test("native structure, whitespace, ranges and inert literal content", async () => {
      for (const selector of [
        "h2",
        "p em",
        "p strong",
        "p > code",
        "p > br[data-text-kind=hardBreak]",
        "blockquote > p",
        "ul > li > ul",
        "ol[start='7']",
        "pre > code",
        "hr",
        "table > thead > tr > th",
        "table > tbody > tr > td",
      ])
        assert.ok(await page.locator(`.markdown-renderer ${selector}`).count(), selector);
      assert.equal(await page.locator("ol > li").nth(1).getAttribute("value"), "8");
      const lines = await page.locator("p").first().innerText();
      assert.ok(lines.includes("Soft line, then a hard break.\nNext line."));
      assert.equal(
        await page
          .locator(".markdown-renderer img, .markdown-renderer script, .markdown-renderer iframe")
          .count(),
        0,
      );
      await page.evaluate(() => {
        const { snapshot } = window.markdownRendererHarness;
        for (const piece of snapshot.pieces.values()) {
          if (piece.kind === "thematicBreak") continue;
          const el = document.querySelector(`[data-piece="${piece.id}"]`)!;
          for (const [key, value] of Object.entries({
            sourceStart: piece.source.start,
            sourceEnd: piece.source.end,
            mapping: piece.mapping,
            part: piece.part,
            continued: piece.continued,
            textKind: piece.kind,
          }))
            if ((el as HTMLElement).dataset[key] !== String(value))
              throw new Error(`Piece ${piece.id}: ${key}`);
          if (piece.kind !== "hardBreak" && el.textContent !== piece.text)
            throw new Error("Piece text changed");
        }
        for (const { context } of snapshot.contexts.values()) {
          const el = document.querySelector<HTMLElement>(`[data-context="${context.id}"]`)!;
          if (
            el.dataset.sourceStart !== String(context.source.start) ||
            el.dataset.sourceEnd !== String(context.source.end) ||
            el.dataset.mapping !== "container"
          )
            throw new Error("Context range changed");
        }
      });
      const nested = await page.locator("ul > li > ul").evaluate((el) => {
        const parentText = el.previousElementSibling!.getBoundingClientRect();
        const nestedText = el.querySelector("p")!.getBoundingClientRect();
        return {
          gap: nestedText.top - parentText.bottom,
          indent: nestedText.left - parentText.left,
          tightMargin: getComputedStyle(el.previousElementSibling!).marginBottom,
        };
      });
      assert.ok(nested.gap >= 4 && nested.gap <= 12, JSON.stringify(nested));
      assert.ok(nested.indent >= 20);
      assert.equal(nested.tightMargin, "0px");
    });
    await t.test(
      "later-page append preserves existing wrappers, text nodes and selection",
      async () => {
        await page.evaluate(async () => {
          const nodes = [...document.querySelectorAll("[data-piece]")];
          const originals = nodes.map((node) => ({ node, text: node.firstChild }));
          const last = nodes.at(-1)!;
          const range = document.createRange();
          range.setStart(last.firstChild!, 0);
          range.setEnd(last.firstChild!, 6);
          const selection = getSelection()!;
          selection.removeAllRanges();
          selection.addRange(range);
          await window.markdownRendererHarness.append();
          for (const original of originals) {
            const current = document.querySelector(
              `[data-piece="${original.node.getAttribute("data-piece")}"]`,
            );
            if (current !== original.node || current?.firstChild !== original.text)
              throw new Error("Appended page replaced an existing node");
          }
          if (selection.toString() !== "Stable") throw new Error("Selection lost on append");
        });
        assert.ok(await page.getByText("later page appended.").count());
      },
    );
    await t.test("keyboard focus, local overflow and narrow/zoom-equivalent layout", async () => {
      for (const width of [390, 600]) {
        await page.setViewportSize({ width, height: width === 600 ? 450 : 844 });
        await assertLocalOverflow(page);
        await page.evaluate(() => {
          (document.activeElement as HTMLElement).blur();
          document.querySelector("pre")!.scrollLeft = 0;
          scrollTo(0, 0);
        });
      }
    });
    await t.test(
      "link elements follow the pure policy exactly; activation remains native",
      async () => {
        await open("links");
        const destinations = await page.evaluate(() => window.markdownRendererHarness.links);
        for (const [index, destination] of destinations.entries()) {
          const label = page.getByText(`link-${index}`, { exact: true });
          const expected = safeMarkdownLink(destination);
          assert.equal(
            await label.evaluate((el) => el.parentElement!.tagName),
            expected === undefined ? "SPAN" : "A",
            destination,
          );
          assert.equal(
            await label.evaluate((el) => el.parentElement!.getAttribute("href")),
            expected ?? null,
            destination,
          );
        }
        await page.getByRole("link", { name: "link-3", exact: true }).focus();
        await page.keyboard.press("Enter");
        assert.equal(new URL(page.url()).hash, "#local");
        await page.getByText("link-4", { exact: true }).click();
        assert.equal(new URL(page.url()).hash, "#local");
      },
    );
    await t.test(
      "middle window hydrates detached headers and complete ancestry with isolated IDs",
      async () => {
        await open("detached");
        assert.equal(await page.locator("table").count(), 1);
        assert.equal(await page.locator("thead th").count(), 3);
        assert.equal(await page.locator("tbody td").count(), 4);
        assert.equal(await page.locator("tbody td").last().innerText(), "");
        await assertHeaders(page);
        assert.equal(
          await page.locator("[data-kind=document] > .markdown-renderer-table > table").count(),
          1,
        );
        await page.evaluate(() => window.markdownRendererHarness.duplicate());
        const ids = await page.locator("th").evaluateAll((headers) => headers.map((el) => el.id));
        assert.ok(ids.every((id) => !/\s/u.test(id)));
        await assertHeaders(page);
        await page.reload();
        await page.waitForSelector('html[data-ready="true"]');
        assert.equal(await page.locator("thead th").first().innerText(), "Name");
      },
    );
    assert.deepEqual(errors, []);
    assert.deepEqual(unexpected, [], "No external resource requests, including literal images");
  } finally {
    await browser.close();
    await server.close();
  }
});

async function assertHeaders(page: Page) {
  await page.locator("tbody td[headers]").evaluateAll((cells) => {
    for (const cell of cells) {
      const header = document.getElementById(cell.getAttribute("headers")!);
      if (
        header?.tagName !== "TH" ||
        header.closest("table") !== cell.closest("table") ||
        header.getAttribute("scope") !== "col" ||
        !header.textContent
      )
        throw new Error("Detached header association missing or colliding");
    }
  });
}

async function assertLocalOverflow(page: Page) {
  assert.ok(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    "Document overflows horizontally",
  );
  assert.ok(
    await page.locator("pre").evaluate((el) => el.scrollWidth > el.clientWidth),
    "Long code should scroll locally",
  );
  await page.locator("pre").focus();
  await page.keyboard.press("ArrowRight");
  await page.waitForFunction(() => document.querySelector("pre")!.scrollLeft > 0);
}
