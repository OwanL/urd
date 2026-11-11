import assert from "node:assert/strict";
import test from "node:test";
import {
  markdownChildGaps,
  markdownRootGaps,
  MarkdownWindowGeometry,
  markdownTableColumns,
  requireMarkdownWindowUnits,
} from "../markdown-rendering/MarkdownWindowGeometry.ts";
import type { MarkdownRenderSnapshot } from "../markdown-rendering/MarkdownRenderModel.ts";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import {
  createMarkdownContext,
  createMarkdownPiece,
  SyntheticMarkdownPageSource,
} from "./MarkdownRenderFixture.ts";

void test("positional gaps partition omitted complete units, not source bytes", () => {
  for (const [count, positions, start, expected] of [
    [10, [], 0, "0:10"],
    [10, [2, 3, 7], 0, "0:2 4:7 8:10"],
    [10, [7, 2, 3, 2], 1, "1:2 4:7 8:10"],
    [3, [0, 1, 2], 0, ""],
  ] as const)
    assert.equal(
      markdownChildGaps(count, positions, start)
        .map(({ from, next }) => `${from}:${next}`)
        .join(" "),
      expected,
    );
});

void test("root piece spans leave gaps around complete loaded child extents", () => {
  assert.deepEqual(
    markdownRootGaps(0, 10, [
      { firstPiece: 7, pieceCount: 3 },
      { firstPiece: 2, pieceCount: 3 },
      { firstPiece: 4, pieceCount: 1 },
      { firstPiece: 12, pieceCount: 1 },
    ]),
    [
      { from: 0, next: 2 },
      { from: 5, next: 7 },
    ],
  );
  assert.deepEqual(
    markdownRootGaps(10, 20, [
      { firstPiece: 14, pieceCount: 3 },
      { firstPiece: 8, pieceCount: 4 },
    ]),
    [
      { from: 12, next: 14 },
      { from: 17, next: 20 },
    ],
  );
});

void test("seek targets map root and ordinary gaps directly to piece IDs", () => {
  const geometry = new MarkdownWindowGeometry();
  geometry.configure("g", 800, "default");
  const scrollport = {
    getBoundingClientRect: () => ({ top: 100, right: 800, bottom: 300, left: 0 }),
  } as unknown as HTMLElement;
  const snapshot = {
    generation: "g",
    from: 0,
    next: 0,
    hasMore: true,
    displayRanges: [],
    supportRanges: [],
    contexts: new Map(),
    pieces: new Map(),
  } satisfies MarkdownRenderSnapshot;

  for (const kind of ["root", "ordinary"] as const) {
    const gap = {
      dataset: { markdownGap: kind, gapFrom: "20", gapNext: "40" },
      getBoundingClientRect: () => ({ top: 150, right: 800, bottom: 250, left: 0, height: 100 }),
    };
    const root = {
      dataset: { markdownGeneration: "g" },
      getBoundingClientRect: () => ({ top: 0, right: 800, bottom: 500, left: 0, width: 800 }),
      querySelectorAll: () => [gap],
    } as unknown as HTMLElement;
    assert.deepEqual(geometry.seekTarget(root, scrollport, snapshot), {
      generation: "g",
      index: 30,
      offset: 100,
    });
  }
});

void test("seek targets map code and table unit gaps through shell piece ranges", () => {
  const geometry = new MarkdownWindowGeometry();
  geometry.configure("g", 800, "default");
  const scrollport = {
    getBoundingClientRect: () => ({ top: 100, right: 800, bottom: 300, left: 0 }),
  } as unknown as HTMLElement;
  const code = createMarkdownContext(7, {
    kind: "code",
    firstPiece: 100,
    pieceCount: 100,
    childCount: 10,
  });
  const table = createMarkdownContext(8, {
    kind: "table",
    firstPiece: 0,
    pieceCount: 110,
    childCount: 11,
  });
  const header = createMarkdownContext(9, {
    kind: "row",
    parent: 8,
    firstPiece: 0,
    pieceCount: 10,
    header: true,
  });
  const snapshot = {
    generation: "g",
    from: 0,
    next: 0,
    hasMore: true,
    displayRanges: [],
    supportRanges: [],
    contexts: new Map([
      [7, { context: code, values: {} }],
      [8, { context: table, values: {} }],
      [9, { context: header, values: {} }],
    ]),
    pieces: new Map(),
  } satisfies MarkdownRenderSnapshot;
  const rootFor = (kind: "code" | "table", from: number, next: number) => {
    const shell = { dataset: { context: kind === "code" ? "7" : "8" } };
    const gap = {
      dataset: { markdownGap: kind, gapFrom: String(from), gapNext: String(next) },
      getBoundingClientRect: () => ({ top: 150, right: 800, bottom: 250, left: 0, height: 100 }),
      closest: () => shell,
    };
    return {
      dataset: { markdownGeneration: "g" },
      getBoundingClientRect: () => ({ top: 0, right: 800, bottom: 500, left: 0, width: 800 }),
      querySelectorAll: () => [gap],
    } as unknown as HTMLElement;
  };

  assert.deepEqual(geometry.seekTarget(rootFor("code", 4, 6), scrollport, snapshot), {
    generation: "g",
    contextId: 7,
    childIndex: 5,
    offset: 100,
  });
  assert.deepEqual(geometry.seekTarget(rootFor("table", 5, 7), scrollport, snapshot), {
    generation: "g",
    contextId: 8,
    childIndex: 6,
    offset: 100,
  });
  assert.equal(
    geometry.seekTarget(rootFor("code", 4, 6), scrollport, { ...snapshot, generation: "stale" }),
    null,
  );
});

void test("code and table gap seeks resolve uneven child extents exactly", async () => {
  const pieces = Array.from({ length: 10 }, (_, id) =>
    createMarkdownPiece(id, id === 0 ? 2 : id < 5 ? 3 : id === 5 ? 5 : id === 6 ? 6 : 7, `p${id}`),
  );
  const source = new SyntheticMarkdownPageSource(
    pieces,
    [
      createMarkdownContext(0, {
        kind: "document",
        firstChild: 1,
        childCount: 2,
        pieceCount: 10,
        nextSibling: -1,
      }),
      createMarkdownContext(1, {
        kind: "code",
        parent: 0,
        firstChild: 2,
        nextSibling: 4,
        childCount: 2,
        pieceCount: 5,
      }),
      createMarkdownContext(2, {
        kind: "codeLine",
        parent: 1,
        nextSibling: 3,
        firstPiece: 0,
        pieceCount: 1,
        position: 0,
      }),
      createMarkdownContext(3, {
        kind: "codeLine",
        parent: 1,
        firstPiece: 1,
        pieceCount: 4,
        position: 1,
      }),
      createMarkdownContext(4, {
        kind: "table",
        parent: 0,
        firstChild: 5,
        childCount: 3,
        firstPiece: 5,
        pieceCount: 5,
      }),
      createMarkdownContext(5, {
        kind: "row",
        parent: 4,
        nextSibling: 6,
        firstPiece: 5,
        pieceCount: 1,
        position: 0,
        header: true,
      }),
      createMarkdownContext(6, {
        kind: "row",
        parent: 4,
        nextSibling: 7,
        firstPiece: 6,
        pieceCount: 1,
        position: 1,
      }),
      createMarkdownContext(7, {
        kind: "row",
        parent: 4,
        firstPiece: 7,
        pieceCount: 3,
        position: 2,
      }),
    ],
    { generation: "g" },
  );
  const code = source.contexts[1]!.context;
  const table = source.contexts[4]!.context;
  const header = source.contexts[5]!.context;
  const snapshot = {
    generation: "g",
    from: 0,
    next: 0,
    hasMore: true,
    displayRanges: [],
    supportRanges: [],
    contexts: new Map([
      [1, { context: code, values: {} }],
      [4, { context: table, values: {} }],
      [5, { context: header, values: {} }],
    ]),
    pieces: new Map(),
  } satisfies MarkdownRenderSnapshot;
  const geometry = new MarkdownWindowGeometry();
  geometry.configure("g", 800, "default");
  const scrollport = {
    getBoundingClientRect: () => ({ top: 100, right: 800, bottom: 300, left: 0 }),
  } as unknown as HTMLElement;
  const rootFor = (kind: "code" | "table", from: number, next: number) => {
    const shell = { dataset: { context: kind === "code" ? "1" : "4" } };
    const gap = {
      dataset: { markdownGap: kind, gapFrom: String(from), gapNext: String(next) },
      getBoundingClientRect: () => ({ top: 150, right: 800, bottom: 250, left: 0, height: 100 }),
      closest: () => shell,
    };
    return {
      dataset: { markdownGeneration: "g" },
      getBoundingClientRect: () => ({ top: 0, right: 800, bottom: 500, left: 0, width: 800 }),
      querySelectorAll: () => [gap],
    } as unknown as HTMLElement;
  };
  const codeTarget = geometry.seekTarget(rootFor("code", 0, 2), scrollport, snapshot);
  const tableTarget = geometry.seekTarget(rootFor("table", 1, 3), scrollport, snapshot);
  assert.deepEqual(codeTarget, { generation: "g", contextId: 1, childIndex: 1, offset: 100 });
  assert.deepEqual(tableTarget, { generation: "g", contextId: 4, childIndex: 2, offset: 100 });
  assert.ok(codeTarget && "contextId" in codeTarget);
  assert.ok(tableTarget && "contextId" in tableTarget);
  assert.deepEqual(
    await source.locateChild(
      codeTarget.contextId,
      codeTarget.childIndex,
      new AbortController().signal,
    ),
    { generation: "g", index: 1 },
  );
  assert.deepEqual(
    await source.locateChild(
      tableTarget.contextId,
      tableTarget.childIndex,
      new AbortController().signal,
    ),
    { generation: "g", index: 7 },
  );
});

void test("geometry is disposable and requires complete positional units", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 2, "line\n")],
    [
      { kind: "document", children: [1, 1], pieces: [0, 1] },
      { kind: "code", parent: 0, children: [2, 4], pieces: [0, 1] },
      { kind: "codeLine", parent: 1, position: 2, pieces: [0, 1] },
    ],
  );
  const snapshot = await new MarkdownWindowAssembler(source, source.manifest).append(
    new AbortController().signal,
  );
  const parent = snapshot.contexts.get(1)!;
  const units = [snapshot.contexts.get(2)!];
  requireMarkdownWindowUnits(snapshot, parent, units);
  assert.throws(
    () => requireMarkdownWindowUnits({ ...snapshot, pieces: new Map() }, parent, units),
    /complete/u,
  );
  assert.throws(
    () => requireMarkdownWindowUnits(snapshot, parent, [...units, ...units]),
    /unique/u,
  );
  const geometry = new MarkdownWindowGeometry();
  assert.throws(() => geometry.configure("g", 0, "default"), /width/u);
  geometry.configure("g", 800, "default");
  assert.equal(geometry.gapHeight("code", 1, 2, 5), 72);
  const lengths = [4, 12];
  const cells = lengths.map((_, position) => ({
    context: createMarkdownContext(position, { position }),
    values: {},
  }));
  assert.deepEqual(
    markdownTableColumns(cells, new Map(lengths.map((length, position) => [position, length]))),
    [25, 75],
  );
});

void test("measurements replace estimates and fence generation, width and layout profile", () => {
  const geometry = new MarkdownWindowGeometry();
  let height = 10;
  const shell = { dataset: { context: "1", kind: "code" } };
  const line = {
    closest: () => shell,
    getBoundingClientRect: () => ({ top: 0, right: 800, bottom: height, left: 0, height }),
  };
  const root = {
    dataset: { markdownGeneration: "g" },
    getBoundingClientRect: () => ({ top: 0, right: 800, bottom: 100, left: 0, width: 800 }),
    querySelectorAll: (selector: string) =>
      selector.includes("data-root-piece-count")
        ? []
        : selector.includes("codeLine")
          ? [line]
          : [shell],
  } as unknown as HTMLElement;
  geometry.configure("g", 800, "a");
  assert.equal(geometry.measure(root), true);
  assert.equal(geometry.measure(root), false);
  assert.equal(geometry.gapHeight("code", 1, 0, 3), 30);
  height = 20;
  assert.equal(geometry.measure(root), true);
  assert.equal(geometry.gapHeight("code", 1, 0, 3), 60);
  for (const [generation, width, profile, accepted] of [
    ["other", 800, "a", false],
    ["g", 400, "a", false],
    ["g", 800, "b", true],
  ] as const) {
    geometry.configure(generation, width, profile);
    assert.equal(geometry.gapHeight("code", 1, 0, 3), 72);
    assert.equal(geometry.measure(root), accepted);
  }
});

void test("root gap height measures complete blocks and collapsed sibling margins", () => {
  const block = (
    pieceCount: number,
    renderedPieces: number,
    height: number,
    marginTop: number,
    marginBottom: number,
  ) => ({
    dataset: { rootPieceCount: String(pieceCount) },
    ownerDocument: {
      defaultView: {
        getComputedStyle: () => ({
          marginTop: `${marginTop}px`,
          marginBottom: `${marginBottom}px`,
        }),
      },
    },
    getBoundingClientRect: () => ({
      top: 10,
      right: 800,
      bottom: 10 + height,
      left: 0,
      height,
    }),
    querySelectorAll: () => Array.from({ length: renderedPieces }),
    previousElementSibling: null as unknown,
    nextElementSibling: null as unknown,
  });
  const blocks = [block(2, 2, 40, 0, 10), block(1, 1, 30, 20, 0), block(4, 1, 900, 100, 100)];
  blocks.forEach((current, index) => {
    current.previousElementSibling = blocks[index - 1] ?? null;
    current.nextElementSibling = blocks[index + 1] ?? null;
  });
  const root = {
    dataset: { markdownGeneration: "g" },
    getBoundingClientRect: () => ({ top: 0, right: 800, bottom: 500, left: 0, width: 800 }),
    querySelectorAll: (selector: string) =>
      selector.includes("data-root-piece-count") ? blocks : selector.includes("codeLine") ? [] : [],
  } as unknown as HTMLElement;
  const geometry = new MarkdownWindowGeometry();
  geometry.configure("g", 800, "default");
  assert.equal(geometry.rootGapHeight(0, 3), 72);
  assert.equal(geometry.measure(root), true);
  assert.ok(Math.abs(geometry.rootGapHeight(0, 3) - 90) < 0.01);
  geometry.configure("g", 800, "resized profile");
  assert.equal(geometry.rootGapHeight(0, 3), 72);
});

void test("shared scrollport clips geometry and restores without scrolling the renderer root", () => {
  const geometry = new MarkdownWindowGeometry();
  const shell = { dataset: { context: "1", kind: "code" } };
  const visibleLine = {
    closest: () => shell,
    getBoundingClientRect: () => ({ top: 90, right: 800, bottom: 110, left: 0, height: 20 }),
  };
  const clippedLine = {
    closest: () => shell,
    getBoundingClientRect: () => ({ top: 20, right: 800, bottom: 40, left: 0, height: 20 }),
  };
  let textTop = 120;
  const piece = {
    dataset: { piece: "7", sourceStart: "14", sourceEnd: "21" },
    textContent: "anchor",
    ownerDocument: {
      createRange: () => ({
        selectNodeContents: () => undefined,
        getClientRects: () => {
          const top = textTop - scrollport.scrollTop;
          return [{ top, right: 200, bottom: top + 10, left: 100 }];
        },
      }),
    },
  };
  let rootTop = 50;
  const root = {
    dataset: { markdownGeneration: "g" },
    scrollTop: 0,
    getBoundingClientRect: () => ({ top: rootTop, right: 900, bottom: 900, left: 100, width: 800 }),
    querySelectorAll: (selector: string) => {
      if (selector.includes("data-root-piece-count")) return [];
      if (selector.includes("codeLine")) return [visibleLine, clippedLine];
      if (selector.includes('[data-kind="code"]')) return [shell];
      return [piece];
    },
  } as unknown as HTMLElement;
  const scrollport = {
    scrollTop: 0,
    getBoundingClientRect: () => ({ top: 100, right: 920, bottom: 400, left: 80, width: 840 }),
  } as unknown as HTMLElement;
  geometry.configure("g", 800, "default");
  assert.equal(geometry.measure(root, scrollport), true);
  assert.equal(geometry.gapHeight("code", 1, 0, 2), 40);

  textTop = 95;
  assert.equal(
    geometry.capture(root, scrollport)?.offset,
    -5,
    "Partially visible text remains a source anchor",
  );
  textTop = 120;
  const anchor = geometry.capture(root, scrollport);
  assert.deepEqual(anchor, {
    generation: "g",
    piece: "7",
    start: "14",
    end: "21",
    offset: 20,
  });
  textTop = 190;
  assert.equal(geometry.restore(root, anchor, scrollport), true);
  assert.equal(scrollport.scrollTop, 70);
  assert.equal(root.scrollTop, 0);

  rootTop = 110;
  const insetAnchor = geometry.capture(root, scrollport);
  assert.equal(
    insetAnchor?.offset,
    20,
    "Source offsets stay relative to the shared port, not the inset body",
  );
  textTop = 230;
  assert.equal(geometry.restore(root, insetAnchor, scrollport), true);
  assert.equal(scrollport.scrollTop, 110);
});
