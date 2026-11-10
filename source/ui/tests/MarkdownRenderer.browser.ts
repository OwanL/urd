import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { chromium, type JSHandle, type Page } from "playwright";
import { createServer } from "vite";
import { safeMarkdownLink } from "../markdown-rendering/MarkdownLinkPolicy.ts";
import type { MarkdownRendererHarness } from "./MarkdownRendererHarness.tsx";

declare global {
  interface Window {
    markdownRendererHarness: MarkdownRendererHarness;
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
      const originals = await pieceNodeSnapshot(page);
      assert.equal(await page.evaluate(() => getSelection()!.toString()), "Stable");
      for (let index = 0; index < 2; index++) {
        await page.evaluate(() => window.markdownRendererHarness.append());
        assert.equal(
          await pieceNodesRetained(page, originals),
          true,
          "Append replaced an existing node",
        );
        assert.equal(
          await page.evaluate(() => getSelection()!.toString()),
          "Stable",
          "Selection lost on append",
        );
      }
      await originals.dispose();
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
          assert.equal(await nodes.evaluate((pieces) => pieces.length), next - first);
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
            await endpoints.evaluate((before) =>
              Object.entries(before).every(
                ([key, value]) => getSelection()![key as keyof Selection] === value,
              ),
            ),
            true,
            "Trim preserves native selection endpoints",
          );
          assert.ok(retained.pieces <= 20 && retained.contexts <= 30);
          assert.ok(retained.elements <= 20 && retained.contextElements <= 30);
          assert.deepEqual(retained.active, [10, 20]);
          assert.ok(retained.ranges.some((range) => range.from <= first && range.next >= next));
          assert.equal(await pieceNodesRetained(page, nodes), true);
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
        assert.equal(await crossingNodes.evaluate((pieces) => pieces.length), 5);
        await page.evaluate(() => window.markdownRendererHarness.trim({ from: 10, next: 20 }));
        assert.equal(await page.evaluate(() => getSelection()!.toString()), outsideSelection);
        assert.equal(await pieceNodesRetained(page, crossingNodes), true);
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
    const evidenceDirectory = await mkdtemp(join(tmpdir(), "urd-markdown-renderer-"));
    const longWindowEvidence: unknown[] = [];
    for (const mode of ["long-code", "long-table"] as const)
      await t.test(`${mode} remains bounded through forward and backward traversal`, async () => {
        longWindowEvidence.push(await exerciseLongWindow(page, mode, evidenceDirectory, open));
      });
    const evidencePath = join(evidenceDirectory, "long-window-evidence.json");
    await writeFile(evidencePath, JSON.stringify(longWindowEvidence, null, 2));
    console.log(`Markdown long-window evidence: ${evidencePath}`);
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

type LongWindowMode = "long-code" | "long-table";

async function exerciseLongWindow(
  page: Page,
  mode: LongWindowMode,
  evidenceDirectory: string,
  open: (mode: string) => Promise<void>,
) {
  await page.setViewportSize({ width: 1000, height: 900 });
  await open(mode);
  const unit = mode === "long-code" ? 1 : 4;
  const inspect = async () => {
    const state = await page.evaluate((kind) => {
      const snapshot = window.markdownRendererHarness.snapshot;
      const root = document.querySelector<HTMLElement>(".markdown-renderer")!;
      const scrollport = document.querySelector<HTMLElement>("#transcript-scrollport")!;
      const gaps = [...root.querySelectorAll<HTMLElement>("[data-markdown-gap]")];
      const code = kind === "long-code";
      const gapsValid = gaps.every((gap) => {
        const cell = gap.firstElementChild as HTMLTableCellElement | null;
        return (
          gap.tagName === (code ? "SPAN" : "TR") &&
          gap.parentElement?.tagName === (code ? "CODE" : "TBODY") &&
          !gap.textContent &&
          !gap.hasAttribute("data-source-start") &&
          !gap.hasAttribute("data-source-end") &&
          gap.getAttribute("aria-hidden") === "true" &&
          getComputedStyle(gap).userSelect === "none" &&
          (code || (cell?.tagName === "TD" && cell.colSpan === 4 && gap.children.length === 1))
        );
      });
      const rows = [...root.querySelectorAll<HTMLElement>("tbody > tr:not([data-markdown-gap])")];
      const lines = [...root.querySelectorAll<HTMLElement>('[data-kind="codeLine"]')];
      const headers = [...root.querySelectorAll<HTMLElement>("thead th")];
      return {
        from: snapshot.from,
        next: snapshot.next,
        pieces: snapshot.pieces.size,
        contexts: snapshot.contexts.size,
        domPieces: root.querySelectorAll("[data-piece]").length,
        domContexts: root.querySelectorAll("[data-context]").length,
        rows: rows.length,
        lines: lines.length,
        gaps: gaps.length,
        gapsValid,
        widths: headers.map((header) => header.getBoundingClientRect().width),
        headerIds: headers.map((header) => header.id),
        headerRowPosition: Number(root.querySelector("thead tr")?.getAttribute("data-position")),
        firstBodyPosition: Number(rows[0]?.dataset.position),
        pageSize: window.markdownRendererHarness.pageSize,
        pieceCount: window.markdownRendererHarness.pieceCount,
        requestCount: window.markdownRendererHarness.pageCursors.length,
        pageCursorsValid: window.markdownRendererHarness.pageCursors.every(
          (cursor) => cursor % (kind === "long-code" ? 1 : 4) === 0,
        ),
        emptyCells: [...root.querySelectorAll("tbody tr td:nth-child(4)")].filter(
          (cell) => !cell.closest("[data-markdown-gap]") && cell.textContent === "",
        ).length,
        positioned: [...(kind === "long-code" ? lines : rows)].every(
          (item) =>
            Number.isInteger(Number(item.dataset.position)) &&
            (kind === "long-code" || Number(item.dataset.position) >= 1),
        ),
        documentWidth: document.documentElement.scrollWidth,
        viewportWidth: innerWidth,
        rootScrollTop: root.scrollTop,
        scrollportScrollTop: scrollport.scrollTop,
        scrollportScrollHeight: scrollport.scrollHeight,
        scrollportClientHeight: scrollport.clientHeight,
        neighbors: [...scrollport.querySelectorAll<HTMLElement>(".neighbor-content")].map(
          (neighbor) => neighbor.textContent,
        ),
        selectionPins: window.markdownRendererHarness.readSelectionPins(),
      };
    }, mode);
    const activeUnits = Math.ceil((state.next - state.from) / unit);
    assert.ok(activeUnits <= 49, JSON.stringify({ mode, state, activeUnits }));
    const bounds = mode === "long-code" ? [52, 56, 56, 60] : [202, 260, 208, 268];
    [state.pieces, state.contexts, state.domPieces, state.domContexts].forEach((count, index) =>
      assert.ok(count <= bounds[index]!, JSON.stringify(state)),
    );
    assert.ok(state.documentWidth <= state.viewportWidth, "Document overflows horizontally");
    assert.equal(state.positioned, true, "Windowed source lines/rows expose data-position");
    assert.ok(state.gapsValid, "Gaps retain native structure and are hidden/non-selectable");
    assert.equal(state.pageSize % unit, 0, "Source page ends on a complete unit");
    assert.equal(state.pageCursorsValid, true, "Source reads start on complete units");
    if (mode === "long-code") assert.ok(state.lines <= 49, JSON.stringify(state));
    else {
      assert.ok(state.rows <= 49, JSON.stringify(state));
      assert.equal(state.headerRowPosition, 0, "Header row has position zero");
      assert.ok(state.emptyCells > 0, "Long table retains empty cells");
      assert.equal(new Set(state.headerIds).size, 4, "Table headers have unique IDs");
      await assertHeaders(page);
    }
    return state;
  };
  const initial = await inspect();
  assert.equal(initial.rootScrollTop, 0, "Markdown renderer root is not a scrollport");
  assert.ok(initial.scrollportScrollHeight > initial.scrollportClientHeight);
  assert.deepEqual(initial.neighbors, ["Earlier transcript content", "Later transcript content"]);
  assert.ok(initial.gaps > 0, "Long content exposes native gap placeholders");
  assert.ok(
    mode === "long-code" ? initial.lines >= 24 : initial.rows >= 24,
    JSON.stringify(initial),
  );
  if (mode === "long-table")
    assert.equal(initial.firstBodyPosition, 1, "First body row follows header position zero");
  if (mode === "long-code") await assertLocalOverflow(page);
  let stableWidths = initial.widths;
  const anchorErrors: number[] = [];
  const directions = { forward: 0, backward: 0 };
  const headerNodes =
    mode === "long-table"
      ? await page.evaluateHandle(() => [...document.querySelectorAll<HTMLElement>("thead th")])
      : null;
  const pinId = mode === "long-code" ? 2 : 12;
  const pinnedText = await page.evaluate((id) => {
    const piece = document.querySelector<HTMLElement>(`[data-piece="${id}"]`)!;
    const range = document.createRange();
    range.selectNodeContents(piece);
    getSelection()!.removeAllRanges();
    getSelection()!.addRange(range);
    return getSelection()!.toString();
  }, pinId);
  const pinnedNodes = await selectedPieceNodes(page, pinId, pinId + 1);
  const windowState = () =>
    page.evaluate(() => ({
      from: window.markdownRendererHarness.snapshot.from,
      next: window.markdownRendererHarness.snapshot.next,
    }));
  const move = async (direction: "forward" | "backward") => {
    const before = await windowState();
    const anchorId = direction === "forward" ? before.next - 12 * unit : before.from + 12 * unit;
    const anchor = page.locator(`[data-piece="${anchorId}"]`);
    await anchor.scrollIntoViewIfNeeded();
    const top = (await anchor.boundingBox())!.y;
    if (directions.forward === 0 && directions.backward === 0)
      assert.ok(
        await page.locator("#transcript-scrollport").evaluate((el) => el.scrollTop > 0),
        "The shared transcript scrollport moves to the anchor",
      );
    await page.evaluate((value) => window.markdownRendererHarness.moveWindow(value), direction);
    const after = await anchor.boundingBox();
    assert.ok(after, `${mode} anchor ${anchorId} was evicted`);
    const error = Math.abs(after!.y - top);
    anchorErrors.push(error);
    // Layout and scrollTop use different subpixel rounding; one CSS pixel is ample.
    assert.ok(error <= 1, `${mode} anchor moved ${error}px`);
    const state = await inspect();
    assert.equal(state.rootScrollTop, 0, "Geometry never scrolls the Markdown renderer root");
    assert.ok(
      state.selectionPins.some(({ from, next }) => from <= pinId && next > pinId),
      "The native selection remains pinned through windowing",
    );
    if (state.from !== before.from || state.next !== before.next) directions[direction]++;
    const overlap = Math.min(before.next, state.next) - Math.max(before.from, state.from);
    assert.ok(overlap >= 20 * unit, `${mode} window overlap was ${overlap}`);
    assert.equal(
      await pieceNodesRetained(page, pinnedNodes),
      true,
      "Windowing replaced a selected node",
    );
    if (mode === "long-table") {
      assert.equal(state.widths.length, stableWidths.length);
      state.widths.forEach((width, index) =>
        assert.ok(Math.abs(width - stableWidths[index]!) <= 1, `Column ${index} width changed`),
      );
      assert.equal(
        await page.evaluate(
          (before) =>
            [...document.querySelectorAll("thead th")].every(
              (node, index) => node === before[index],
            ),
          headerNodes!,
        ),
        true,
        "Windowing preserves header nodes",
      );
    }
    return state;
  };
  for (let step = 0; step < 4; step++) await move("forward");
  const pinned = await page.evaluate(() => ({
    text: getSelection()!.toString(),
    ranges: window.markdownRendererHarness.snapshot.displayRanges,
  }));
  assert.equal(pinned.text, pinnedText, "Disjoint selection remains native");
  assert.ok(pinned.ranges.some(({ from, next }) => from <= pinId && next > pinId));
  assert.equal(await pinnedNodes.evaluate((pieces) => pieces.length), 1);
  assert.equal(await pieceNodesRetained(page, pinnedNodes), true);
  assert.equal(await page.locator('[data-piece="40"]').count(), 0, "Unrelated source was evicted");
  const active = await windowState();
  assert.deepEqual(
    await page
      .locator("[data-markdown-gap]")
      .evaluateAll((gaps) =>
        gaps.map((gap) => [
          Number(gap.getAttribute("data-gap-from")),
          Number(gap.getAttribute("data-gap-next")),
        ]),
      ),
    [
      [mode === "long-code" ? 0 : 1, pinId / unit],
      [pinId / unit + 1, active.from / unit],
      [active.next / unit, mode === "long-code" ? 512 : 513],
    ],
    "Leading, interior and trailing gaps exactly partition omitted units",
  );
  await move("backward");
  const resizeId = (await windowState()).from + 20 * unit;
  const resizeAnchor = page.locator(`[data-piece="${resizeId}"]`);
  await resizeAnchor.scrollIntoViewIfNeeded();
  const resizeTop = (await resizeAnchor.boundingBox())!.y;
  await page.evaluate(() => window.markdownRendererHarness.captureAnchor());
  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => window.markdownRendererHarness.reconfigureGeometry());
  const resizeError = Math.abs((await resizeAnchor.boundingBox())!.y - resizeTop);
  anchorErrors.push(resizeError);
  assert.ok(resizeError <= 1, `${mode} resize anchor moved`);
  const afterResize = await inspect();
  assert.equal(afterResize.rootScrollTop, 0, "Resize does not scroll the Markdown renderer root");
  assert.ok(afterResize.selectionPins.some(({ from, next }) => from <= pinId && next > pinId));
  stableWidths = afterResize.widths;
  const widthScreenshot = join(evidenceDirectory, `${mode}-width-640.png`);
  await page.screenshot({ path: widthScreenshot, fullPage: true });
  let longTasks: number[] = [];
  const supported = await page.evaluate(() =>
    PerformanceObserver.supportedEntryTypes.includes("longtask"),
  );
  if (supported)
    await page.evaluate(() => {
      const durations: number[] = [];
      const observer = new PerformanceObserver((entries) =>
        durations.push(...entries.getEntries().map((entry) => entry.duration)),
      );
      observer.observe({ type: "longtask", buffered: false });
      Object.assign(window, {
        markdownLongTaskObserver: observer,
        markdownLongTaskDurations: durations,
      });
    });
  const traversed: (typeof initial)[] = [];
  for (const direction of ["forward", "backward"] as const)
    for (let step = 0; step < 12; step++) traversed.push(await move(direction));
  let state = traversed.at(-1)!;
  for (const direction of ["forward", "backward"] as const) {
    const remaining = () => (direction === "forward" ? state.pieceCount - state.next : state.from);
    while (remaining() > 0) {
      const previous = remaining();
      state = await move(direction);
      traversed.push(state);
      assert.ok(remaining() < previous, `${direction} traversal did not advance`);
    }
    const requests = state.requestCount;
    state = await move(direction);
    traversed.push(state);
    assert.equal(remaining(), 0, `${direction} traversal stays at its boundary`);
    assert.equal(state.requestCount, requests, "Boundary does not request another page");
  }
  if (supported)
    longTasks = await page.evaluate(async () => {
      await new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      );
      const target = window as unknown as Window & {
        markdownLongTaskObserver: PerformanceObserver;
        markdownLongTaskDurations: number[];
      };
      target.markdownLongTaskObserver.disconnect();
      return target.markdownLongTaskDurations;
    });
  assert.deepEqual(longTasks, [], `${mode} produced >50ms long tasks`);
  assert.ok(traversed.some(({ next }) => next / unit > 360));
  assert.ok(traversed.some(({ from }) => from > 0));
  const afterScreenshot = join(evidenceDirectory, `${mode}-after.png`);
  await page.screenshot({ path: afterScreenshot, fullPage: true });
  await pinnedNodes.dispose();
  await headerNodes?.dispose();
  return {
    mode,
    screenshots: { width: widthScreenshot, after: afterScreenshot },
    directions,
    initial: {
      pieces: initial.pieces,
      contexts: initial.contexts,
      lines: initial.lines,
      rows: initial.rows,
    },
    maximumAnchorError: Math.max(...anchorErrors),
    maximum: {
      pieces: Math.max(...traversed.map(({ pieces }) => pieces)),
      contexts: Math.max(...traversed.map(({ contexts }) => contexts)),
      domPieces: Math.max(...traversed.map(({ domPieces }) => domPieces)),
      domContexts: Math.max(...traversed.map(({ domContexts }) => domContexts)),
    },
    longTaskObserverSupported: supported,
    longTasks,
  };
}

type SelectedPiece = { id: string; element: HTMLElement; text: Node | null };

async function pieceNodeSnapshot(
  page: Page,
  range?: { from: number; next: number },
): Promise<JSHandle<SelectedPiece[]>> {
  return page.evaluateHandle(
    ({ from, next, selected }) => {
      const selection = selected ? getSelection() : null;
      if (selected && !selection?.rangeCount) throw new Error("Expected selected pieces");
      const selectedRange = selection?.getRangeAt(0);
      return [...document.querySelectorAll<HTMLElement>(".markdown-renderer [data-piece]")]
        .filter((piece) => {
          const id = Number(piece.dataset.piece);
          return id >= from && id < next && (!selectedRange || selectedRange.intersectsNode(piece));
        })
        .map((element) => ({ id: element.dataset.piece!, element, text: element.firstChild }));
    },
    {
      from: range?.from ?? 0,
      next: range?.next ?? Number.MAX_SAFE_INTEGER,
      selected: range !== undefined,
    },
  );
}

function selectedPieceNodes(page: Page, from = 0, next = Number.MAX_SAFE_INTEGER) {
  return pieceNodeSnapshot(page, { from, next });
}

function normalizeSelection(text: string): string {
  return text.replace(/\r\n/gu, "\n");
}

async function pieceNodesRetained(page: Page, pieces: JSHandle<SelectedPiece[]>): Promise<boolean> {
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
