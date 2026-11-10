import assert from "node:assert/strict";
import test from "node:test";
import { chromium, type JSHandle, type Page } from "playwright";
import { createServer } from "vite";
import { safeMarkdownLink } from "../markdown-rendering/MarkdownLinkPolicy.ts";
import type {
  MarkdownPieceInterval,
  MarkdownRenderSnapshot,
} from "../markdown-rendering/MarkdownRenderModel.ts";

declare global {
  interface Window {
    markdownRendererHarness: {
      readonly links: string[];
      readonly snapshot: MarkdownRenderSnapshot;
      readonly selectionChanges: number;
      readSelectionPins(): readonly MarkdownPieceInterval[];
      trim(active: MarkdownPieceInterval): Promise<void>;
      disposeSelectionPins(): void;
      append(): Promise<void>;
      unmountRemount(): void;
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
    const context = await browser.newContext({ viewport: { width: 1000, height: 900 } });
    await context.grantPermissions(["clipboard-read", "clipboard-write"], {
      origin: new URL(origin).origin,
    });
    const page = await context.newPage();
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
      "keyboard extension creates native selection across inline semantics",
      async () => {
        await page.getByText("Readable", { exact: true }).dblclick();
        await page.keyboard.press("Shift+End");
        const selection = await page.evaluate(() => {
          const selected = getSelection()!;
          const range = selected.getRangeAt(0);
          return {
            text: selected.toString(),
            pieces: [...document.querySelectorAll<HTMLElement>(".markdown-renderer [data-piece]")]
              .filter((piece) => range.intersectsNode(piece))
              .map((piece) => piece.dataset.mapping),
            contexts: [
              ...document.querySelectorAll<HTMLElement>(".markdown-renderer [data-context]"),
            ]
              .filter((context) => range.intersectsNode(context))
              .map((context) => context.dataset.kind),
          };
        });
        for (const text of ["Readable", "Emphasis", "strong", "inline_code"])
          assert.ok(selection.text.includes(text), selection.text);
        for (const kind of ["emphasis", "strong", "inlineCode"])
          assert.ok(selection.contexts.includes(kind), selection.contexts.join(","));
        assert.ok(
          selection.pieces.length > 1 && selection.pieces.every((mapping) => mapping === "exact"),
        );
        assert.equal(await copySelection(page), selection.text);
      },
    );
    await t.test("content-page updates preserve native selection and existing nodes", async () => {
      await page.getByText("Stable", { exact: true }).dblclick();
      await page.keyboard.press("Shift+ArrowLeft");
      await page.evaluate(async () => {
        const originals = [...document.querySelectorAll<HTMLElement>("[data-piece]")].map(
          (node) => ({
            node,
            text: node.firstChild,
          }),
        );
        const selection = getSelection()!;
        if (selection.toString() !== "Stable") throw new Error("Expected native selection");
        for (let page = 0; page < 2; page++) {
          await window.markdownRendererHarness.append();
          for (const original of originals) {
            const current = document.querySelector(`[data-piece="${original.node.dataset.piece}"]`);
            if (current !== original.node || current?.firstChild !== original.text)
              throw new Error("Appended page replaced an existing node");
          }
          if (selection.toString() !== "Stable") throw new Error("Selection lost on append");
        }
      });
      assert.ok(await page.getByText("later page appended.").count());
    });
    await t.test("selection pins preserve native ranges through bounded trim", async () => {
      try {
        await open("selection");
        const pinIntervals = () =>
          page.evaluate(() => window.markdownRendererHarness.readSelectionPins());
        const select = async (from: number, to: number) => {
          const first = page.locator(`[data-piece="${from}"]`);
          const last = page.locator(`[data-piece="${to}"]`);
          await first.scrollIntoViewIfNeeded();
          const a = (await first.boundingBox())!;
          const b = (await last.boundingBox())!;
          const reverse = from > to;
          await page.mouse.move(reverse ? a.x + a.width - 1 : a.x + 1, a.y + a.height / 2);
          await page.mouse.down();
          await page.mouse.move(reverse ? b.x + 1 : b.x + b.width - 1, b.y + b.height / 2, {
            steps: 10,
          });
          await page.mouse.up();
          assert.notEqual(await page.evaluate(() => getSelection()!.isCollapsed), true);
        };

        await page.evaluate(() => {
          const text = document.querySelector('[data-piece="300"]')!.firstChild!;
          const range = document.createRange();
          range.setStart(text, 0);
          const selection = getSelection()!;
          selection.removeAllRanges();
          selection.addRange(range);
          const pins = window.markdownRendererHarness.readSelectionPins();
          if (pins.length !== 1 || pins[0]!.from !== 300 || pins[0]!.next !== 301)
            throw new Error("Collapsed native focus must pin its complete piece");
          range.setEnd(text, "Selection ".length);
        });
        assert.equal(await page.evaluate(() => getSelection()!.toString()), "Selection ");
        assert.deepEqual(await pinIntervals(), [{ from: 300, next: 301 }]);
        await page.keyboard.press("Shift+ArrowRight");
        assert.equal(await page.evaluate(() => getSelection()!.toString()), "Selection p");
        await page.evaluate(() => window.markdownRendererHarness.trim({ from: 10, next: 20 }));
        assert.equal(await page.evaluate(() => getSelection()!.toString()), "Selection p");
        assert.deepEqual(await pinIntervals(), [{ from: 300, next: 301 }]);
        await page.keyboard.press("Shift+ArrowRight");
        assert.equal(await page.evaluate(() => getSelection()!.toString()), "Selection pa");
        assert.deepEqual(await pinIntervals(), [{ from: 300, next: 301 }]);
        assert.equal(await copySelection(page), "Selection pa");

        const assertDirectionalTrim = async (from: number, to: number) => {
          await open("selection");
          const before = await page.evaluate(() => window.markdownRendererHarness.selectionChanges);
          await select(from, to);
          await page.waitForFunction(
            (previous) => window.markdownRendererHarness.selectionChanges > previous,
            before,
          );
          const first = Math.min(from, to);
          const next = Math.max(from, to) + 1;
          const selected = await page.evaluate(() => getSelection()!.toString());
          assert.ok(
            selected.includes(`Selection paragraph ${first}.`) &&
              selected.includes(`Selection paragraph ${next - 1}.`),
          );
          assert.deepEqual(await pinIntervals(), [{ from: first, next }]);
          assert.equal(normalizeSelection(await copySelection(page)), normalizeSelection(selected));
          const nodes = await selectedPieceNodes(page, first, next);
          const endpoints = await page.evaluateHandle(() => {
            const selection = getSelection()!;
            return {
              anchorNode: selection.anchorNode,
              anchorOffset: selection.anchorOffset,
              focusNode: selection.focusNode,
              focusOffset: selection.focusOffset,
            };
          });
          assert.equal(await selectedPieceCount(page, nodes), next - first);
          await page.evaluate(() => window.markdownRendererHarness.trim({ from: 10, next: 20 }));
          const retained = await page.evaluate(() => ({
            selected: getSelection()!.toString(),
            pieces: window.markdownRendererHarness.snapshot.pieces.size,
            contexts: window.markdownRendererHarness.snapshot.contexts.size,
            elements: document.querySelectorAll(".markdown-renderer [data-piece]").length,
            contextElements: document.querySelectorAll(".markdown-renderer [data-context]").length,
            active: [
              window.markdownRendererHarness.snapshot.from,
              window.markdownRendererHarness.snapshot.next,
            ],
            ranges: [
              ...window.markdownRendererHarness.snapshot.displayRanges,
              ...window.markdownRendererHarness.snapshot.supportRanges,
            ],
          }));
          assert.equal(normalizeSelection(retained.selected), normalizeSelection(selected));
          assert.equal(
            await page.evaluate((before) => {
              const current = getSelection()!;
              return (
                current.anchorNode === before.anchorNode &&
                current.anchorOffset === before.anchorOffset &&
                current.focusNode === before.focusNode &&
                current.focusOffset === before.focusOffset
              );
            }, endpoints),
            true,
            "Trim preserves native selection endpoints",
          );
          assert.ok(retained.pieces <= 20 && retained.contexts <= 30);
          assert.ok(retained.elements <= 20 && retained.contextElements <= 30);
          assert.deepEqual(retained.active, [10, 20]);
          assert.ok(retained.ranges.some((range) => range.from <= first && range.next >= next));
          assert.equal(await selectedPiecesRetained(page, nodes), true);
          assert.equal(await page.locator('[data-piece="100"]').count(), 0);
          assert.equal(normalizeSelection(await copySelection(page)), normalizeSelection(selected));
          await endpoints.dispose();
          await nodes.dispose();

          const changes = await page.evaluate(
            () => window.markdownRendererHarness.selectionChanges,
          );
          await page.evaluate(() => getSelection()!.removeAllRanges());
          await page.waitForFunction(
            (previous) => window.markdownRendererHarness.selectionChanges > previous,
            changes,
          );
          assert.deepEqual(await pinIntervals(), []);
          await page.evaluate(() => window.markdownRendererHarness.trim({ from: 10, next: 20 }));
          assert.equal(await page.locator(`[data-piece="${first}"]`).count(), 0);
          assert.ok(
            await page.evaluate(() => window.markdownRendererHarness.snapshot.pieces.size <= 10),
          );
        };

        for (const [from, to] of [
          [340, 344],
          [355, 351],
        ] as const)
          await assertDirectionalTrim(from, to);

        await open("selection");
        const outsideSelection = await page.evaluate(() => {
          const root = document.querySelector<HTMLElement>(".markdown-renderer")!;
          const text = document.querySelector('[data-piece="344"]')!.firstChild!;
          const parent = root.parentNode!;
          const range = document.createRange();
          range.setStart(parent, [...parent.childNodes].indexOf(root));
          range.setEnd(text, text.textContent!.length);
          const selection = getSelection()!;
          selection.removeAllRanges();
          selection.addRange(range);
          return selection.toString();
        });
        assert.ok(outsideSelection.includes("Selection paragraph 344."));
        assert.ok(
          (await pinIntervals()).some(({ from, next }) => from <= 340 && next >= 345),
          "Selection pins intersecting root content even when an endpoint is outside it",
        );
        const crossingNodes = await selectedPieceNodes(page, 340, 345);
        assert.equal(await selectedPieceCount(page, crossingNodes), 5);
        await page.evaluate(() => window.markdownRendererHarness.trim({ from: 10, next: 20 }));
        assert.equal(await page.evaluate(() => getSelection()!.toString()), outsideSelection);
        assert.equal(await selectedPiecesRetained(page, crossingNodes), true);
        await crossingNodes.dispose();
        await page.evaluate(() => window.markdownRendererHarness.disposeSelectionPins());
        const disposed = await page.evaluate(() => window.markdownRendererHarness.selectionChanges);
        await page.evaluate(() => {
          const text = document.querySelector('[data-piece="12"]')!.firstChild!;
          const range = document.createRange();
          range.setStart(text, 0);
          range.collapse(true);
          getSelection()!.addRange(range);
        });
        await page.waitForTimeout(50);
        assert.equal(
          await page.evaluate(() => window.markdownRendererHarness.selectionChanges),
          disposed,
        );
      } finally {
        await open();
      }
    });
    await t.test(
      "full unmount/remount drops native selection but keeps exact source mapping",
      async () => {
        await page.getByText("Stable", { exact: true }).dblclick();
        await page.keyboard.press("Shift+ArrowLeft");
        const before = await page.evaluate(() => {
          const selected = getSelection()!;
          const piece = (selected.anchorNode as Element).parentElement!.closest<HTMLElement>(
            "[data-piece]",
          )!;
          return {
            text: selected.toString(),
            id: piece.dataset.piece,
            start: piece.dataset.sourceStart,
            end: piece.dataset.sourceEnd,
            mapping: piece.dataset.mapping,
          };
        });
        assert.equal(before.text, "Stable");
        assert.equal(before.mapping, "exact");
        await page.evaluate(() => window.markdownRendererHarness.unmountRemount());
        const after = await page.evaluate((id) => {
          const piece = document.querySelector<HTMLElement>(`[data-piece="${id}"]`)!;
          return {
            text: getSelection()!.toString(),
            start: piece.dataset.sourceStart,
            end: piece.dataset.sourceEnd,
            mapping: piece.dataset.mapping,
          };
        }, before.id);
        assert.equal(
          after.text,
          "",
          "Native selection cannot survive component removal without owner restoration",
        );
        assert.deepEqual(
          { start: after.start, end: after.end, mapping: after.mapping },
          { start: before.start, end: before.end, mapping: "exact" },
        );
      },
    );
    await t.test(
      "native clipboard copy preserves plain text for list, table and code",
      async () => {
        await page.getByText("First", { exact: true }).dblclick();
        await page.keyboard.press("Shift+End");
        assert.equal(await copySelection(page), "First tight item");

        const firstCell = page.locator("tbody tr").first().locator("td [data-piece]").first();
        const lastCell = page.locator("tbody tr").first().locator("td [data-piece]").nth(2);
        const firstBox = (await firstCell.boundingBox())!;
        const lastBox = (await lastCell.boundingBox())!;
        const y = firstBox.y + firstBox.height / 2;
        await page.mouse.move(firstBox.x + 1, y);
        await page.mouse.down();
        await page.mouse.move(lastBox.x + lastBox.width - 1, lastBox.y + lastBox.height / 2, {
          steps: 12,
        });
        await page.mouse.up();
        const row = await page.evaluate(() => getSelection()!.toString());
        assert.equal(row, "Middle-window row\tA detached header remains associated.\t42");
        assert.equal(await copySelection(page), row);

        await page.getByText("const", { exact: true }).dblclick();
        await page.keyboard.press("Shift+End");
        assert.equal(await copySelection(page), "const compass = '🧭';");
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

type SelectedPiece = { id: string; element: HTMLElement; text: Node | null };

async function selectedPieceNodes(
  page: Page,
  from = 0,
  next = Number.MAX_SAFE_INTEGER,
): Promise<JSHandle<SelectedPiece[]>> {
  return page.evaluateHandle(
    ({ from, next }) => {
      const selection = getSelection();
      if (!selection?.rangeCount) throw new Error("Expected selected pieces");
      const range = selection.getRangeAt(0);
      return [...document.querySelectorAll<HTMLElement>(".markdown-renderer [data-piece]")]
        .filter((piece) => {
          const id = Number(piece.dataset.piece);
          return id >= from && id < next && range.intersectsNode(piece);
        })
        .map((element) => ({ id: element.dataset.piece!, element, text: element.firstChild }));
    },
    { from, next },
  );
}

function normalizeSelection(text: string): string {
  return text.replace(/\r\n/gu, "\n");
}

async function selectedPieceCount(page: Page, pieces: JSHandle<SelectedPiece[]>): Promise<number> {
  return page.evaluate((selected) => selected.length, pieces);
}

async function selectedPiecesRetained(
  page: Page,
  pieces: JSHandle<SelectedPiece[]>,
): Promise<boolean> {
  return page.evaluate(
    (selected) =>
      selected.every(({ id, element, text }) => {
        const current = document.querySelector(`[data-piece="${id}"]`);
        return current === element && current?.firstChild === text;
      }),
    pieces,
  );
}

async function copySelection(page: Page): Promise<string> {
  await page.keyboard.press("Control+C");
  return page.evaluate(() => navigator.clipboard.readText());
}

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
