import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import type { MarkdownPiecePage } from "../markdown-rendering/MarkdownRenderModel.ts";
import { safeMarkdownLink } from "../markdown-rendering/MarkdownLinkPolicy.ts";
import {
  createMarkdownContext,
  createMarkdownPiece,
  createMarkdownValue,
  SyntheticMarkdownPageSource,
  type MarkdownRenderFixtureOptions,
} from "./MarkdownRenderFixture.ts";

function signal() {
  return new AbortController().signal;
}

function assertUncommitted(assembler: MarkdownWindowAssembler, from: number) {
  const snapshot = assembler.snapshot;
  assert.equal(snapshot.from, from);
  assert.equal(snapshot.next, from);
  assert.equal(snapshot.pieces.size, 0);
  assert.equal(snapshot.contexts.size, 0);
}

function linearSource(
  pieces: ReturnType<typeof createMarkdownPiece>[],
  options: MarkdownRenderFixtureOptions = {},
) {
  const pieceCount =
    options.pieceCount ?? pieces.reduce((count, piece) => Math.max(count, piece.id + 1), 0);
  return new SyntheticMarkdownPageSource(
    pieces,
    [
      {
        context: createMarkdownContext(0, {
          kind: "document",
          firstChild: 1,
          childCount: 1,
          firstPiece: 0,
          pieceCount,
        }),
      },
      {
        context: createMarkdownContext(1, { parent: 0, firstPiece: 0, pieceCount }),
      },
    ],
    options,
  );
}

void test("assembles continued values and pieces across appended pages", async () => {
  const source = new SyntheticMarkdownPageSource(
    [
      createMarkdownPiece(0, 1, "first ", { part: 0, continued: true }),
      createMarkdownPiece(1, 1, "second", { part: 1 }),
    ],
    [
      {
        context: createMarkdownContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 0,
          pieceCount: 2,
        }),
      },
      {
        context: createMarkdownContext(1, {
          parent: 0,
          firstPiece: 0,
          pieceCount: 2,
          valueCount: 2,
        }),
        values: [
          createMarkdownValue("destination", "/example/", 0, true),
          createMarkdownValue("destination", "target", 1),
        ],
      },
    ],
    { piecePageSize: 1, valuePageSize: 1 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  const first = await assembler.append(signal());
  assert.equal(first.next, 1);
  assert.equal(first.hasMore, true);
  assert.deepEqual([...first.pieces.keys()], [0]);

  const second = await assembler.append(signal());
  assert.equal(second.next, 2);
  assert.equal(second.hasMore, false);
  assert.deepEqual([...second.pieces.keys()], [0, 1]);
  assert.equal(second.contexts.get(1)?.values.destination, "/example/target");
  assert.ok(
    source.contextRequests.some((request) => request.id === 1 && request.cursor.index === 1),
  );
  assert.ok(source.pieceRequests.some((request) => request.index === 1));
});

void test("accepts a middle-window initial part and continues it on the next append", async () => {
  const source = new SyntheticMarkdownPageSource(
    [
      createMarkdownPiece(3, 1, "middle ", { part: 4, continued: true }),
      createMarkdownPiece(4, 1, "end", { part: 5 }),
    ],
    [
      {
        context: createMarkdownContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 3,
          pieceCount: 2,
        }),
      },
      {
        context: createMarkdownContext(1, { parent: 0, firstPiece: 3, pieceCount: 2 }),
      },
    ],
    { piecePageSize: 1 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 3);

  const middle = await assembler.append(signal());
  assert.equal(middle.from, 3);
  assert.equal(middle.next, 4);
  assert.deepEqual([...middle.pieces.keys()], [3]);

  const end = await assembler.append(signal());
  assert.equal(end.next, 5);
  assert.equal(end.hasMore, false);
  assert.deepEqual([...end.pieces.keys()], [3, 4]);

  const invalid = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "continued", { part: 1 })],
    [],
    { contextCount: 2 },
  );
  await assert.rejects(
    new MarkdownWindowAssembler(invalid, invalid.manifest).append(signal()),
    /incomplete text piece/u,
  );
});

void test("prepends ascending pages and validates continuation across the window seam", async () => {
  const source = linearSource(
    Array.from({ length: 5 }, (_, id) =>
      createMarkdownPiece(id, 1, `part-${id}`, { part: id, continued: id < 4 }),
    ),
    { piecePageSize: 1 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 3);

  await assembler.append(signal());
  await assembler.append(signal());
  const initial = assembler.snapshot;
  assert.equal(initial.from, 3);
  assert.equal(initial.next, 5);
  assert.equal(initial.hasMore, false);

  const last = await assembler.prepend(signal());
  assert.equal(last.from, 2);
  assert.equal(last.next, 5);
  assert.equal(last.hasMore, false);
  const middle = await assembler.prepend(signal());
  const first = await assembler.prepend(signal());
  assert.equal(middle.from, 1);
  assert.equal(first.from, 0);
  assert.deepEqual(
    [...first.pieces.keys()].sort((left, right) => left - right),
    [0, 1, 2, 3, 4],
  );
  assert.deepEqual(
    source.beforePieceRequests.map((cursor) => cursor.index),
    [3, 2, 1],
  );
  assert.equal(await assembler.prepend(signal()), first);
});

void test("failed prepend leaves its prior snapshot usable", async () => {
  const cases = [
    {
      name: "stale generation",
      options: { beforePieceGeneration: "stale-generation" },
      message: /generation changed/u,
    },
    {
      name: "malformed cursor",
      options: {
        transformBeforePiecePage: (page: MarkdownPiecePage) => ({ ...page, next: page.next - 1 }),
      },
      message: /preceding page/u,
    },
    {
      name: "continuation seam",
      options: {},
      message: /continuation is discontinuous/u,
    },
  ] as const;

  for (const { name, options, message } of cases) {
    const source = linearSource(
      [
        createMarkdownPiece(0, 1, "first", { part: 0, continued: true }),
        createMarkdownPiece(1, 1, "middle", { part: name === "continuation seam" ? 2 : 1 }),
        createMarkdownPiece(2, 1, "last", { part: name === "continuation seam" ? 3 : 2 }),
      ],
      { piecePageSize: 1, ...options },
    );
    const assembler = new MarkdownWindowAssembler(source, source.manifest, 2);
    await assembler.append(signal());
    const prior = assembler.snapshot;

    await assert.rejects(assembler.prepend(signal()), message, name);
    assert.equal(assembler.snapshot, prior);
    assert.deepEqual([...prior.pieces.keys()], [2]);
  }
});

void test("aborted prepend preserves the snapshot", async () => {
  const source = linearSource([
    createMarkdownPiece(0, 1, "first"),
    createMarkdownPiece(1, 1, "last"),
  ]);
  let release!: () => void;
  let entered!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const started = new Promise<void>((resolve) => (entered = resolve));
  const heldSource = {
    readPieces: source.readPieces.bind(source),
    async readPiecesBefore(cursor: Parameters<typeof source.readPiecesBefore>[0]) {
      entered();
      await gate;
      return source.readPiecesBefore(cursor, signal());
    },
    readContext: source.readContext.bind(source),
  };
  const assembler = new MarkdownWindowAssembler(heldSource, source.manifest, 1);
  await assembler.append(signal());
  const prior = assembler.snapshot;
  const controller = new AbortController();
  const pending = assembler.prepend(controller.signal);
  await started;
  controller.abort();
  release();

  await assert.rejects(pending);
  assert.equal(assembler.snapshot, prior);
});

void test("prepend and append share one pending read", async () => {
  const source = linearSource([
    createMarkdownPiece(0, 1, "first"),
    createMarkdownPiece(1, 1, "last"),
  ]);
  let release!: () => void;
  let entered!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const started = new Promise<void>((resolve) => (entered = resolve));
  const heldSource = {
    manifest: source.manifest,
    pieces: source.pieces,
    contexts: source.contexts,
    pieceRequests: source.pieceRequests,
    beforePieceRequests: source.beforePieceRequests,
    contextRequests: source.contextRequests,
    readPieces: source.readPieces.bind(source),
    async readPiecesBefore(
      cursor: Parameters<typeof source.readPiecesBefore>[0],
      signal: AbortSignal,
    ) {
      entered();
      await gate;
      return source.readPiecesBefore(cursor, signal);
    },
    readContext: source.readContext.bind(source),
  };
  const assembler = new MarkdownWindowAssembler(heldSource, source.manifest, 1);
  const pending = assembler.prepend(signal());
  await started;
  await assert.rejects(assembler.append(signal()), /already pending/u);
  release();
  assert.equal((await pending).from, 0);
});

void test("hydrates middle ancestry and a detached table header cell and its pieces", async () => {
  const contexts = [
    {
      context: createMarkdownContext(0, {
        kind: "document",
        firstChild: 1,
        firstPiece: 2,
        pieceCount: 9,
      }),
    },
    {
      context: createMarkdownContext(1, {
        kind: "table",
        parent: 0,
        firstChild: 2,
        firstPiece: 2,
        pieceCount: 9,
      }),
    },
    {
      context: createMarkdownContext(2, {
        kind: "row",
        parent: 1,
        firstChild: 3,
        nextSibling: 4,
        firstPiece: 2,
        pieceCount: 8,
        header: true,
      }),
    },
    {
      context: createMarkdownContext(3, {
        kind: "cell",
        parent: 2,
        firstPiece: 2,
        pieceCount: 8,
        header: true,
        headerCell: 3,
      }),
    },
    {
      context: createMarkdownContext(4, {
        kind: "row",
        parent: 1,
        firstChild: 5,
        firstPiece: 10,
        pieceCount: 1,
      }),
    },
    {
      context: createMarkdownContext(5, {
        kind: "cell",
        parent: 4,
        firstPiece: 10,
        pieceCount: 1,
        headerCell: 3,
      }),
    },
  ];
  const headerPieces = Array.from({ length: 8 }, (_, offset) =>
    createMarkdownPiece(2 + offset, 3, offset === 0 ? "heading" : " header"),
  );
  const source = new SyntheticMarkdownPageSource(
    [...headerPieces, createMarkdownPiece(10, 5, "value")],
    contexts,
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 10);

  const snapshot = await assembler.append(signal());

  for (const id of [0, 1, 2, 3, 4, 5]) assert.ok(snapshot.contexts.has(id), `context ${id}`);
  assert.ok(snapshot.pieces.has(2), "detached header piece");
  assert.ok(snapshot.pieces.has(10), "window piece");
});

void test("rejects a piece page from another generation without committing", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "text")],
    [{ context: createMarkdownContext(1, { firstPiece: 0, pieceCount: 1 }) }],
    { pieceGeneration: "stale-generation" },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a context page from another generation without committing", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "text")],
    [
      {
        context: createMarkdownContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 1 }),
        values: [createMarkdownValue("destination", "/target")],
      },
    ],
    { contextGeneration: "stale-generation" },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a nonadvancing piece page without committing", async () => {
  const source = new SyntheticMarkdownPageSource([], [], {
    pieceCount: 1,
    transformPiecePage: (page) => ({ ...page, next: page.from, hasMore: true }),
  });
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a nonadvancing context page without committing", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "text")],
    [
      {
        context: createMarkdownContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 2 }),
        values: [
          createMarkdownValue("destination", "/first/", 0, true),
          createMarkdownValue("destination", "last", 1),
        ],
      },
    ],
    {
      valuePageSize: 1,
      transformContextPage: (page) => ({ ...page, next: page.from, hasMore: true }),
    },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects skipped piece IDs without committing", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(2, 1, "skipped")],
    [
      {
        context: createMarkdownContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 0,
          pieceCount: 3,
        }),
      },
      { context: createMarkdownContext(1, { parent: 0, firstPiece: 0, pieceCount: 3 }) },
    ],
    { transformPiecePage: (page) => ({ ...page, next: page.from + page.pieces.length }) },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects discontinuous context value parts without committing", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "text")],
    [
      {
        context: createMarkdownContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 2 }),
        values: [
          createMarkdownValue("destination", "/first/", 0, true),
          createMarkdownValue("destination", "last", 2),
        ],
      },
    ],
    { valuePageSize: 1 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()));
  assertUncommitted(assembler, 0);
});

void test("rejects a header reference from another table", async () => {
  const contexts = [
    {
      context: createMarkdownContext(0, {
        kind: "document",
        firstChild: 1,
        firstPiece: 0,
        pieceCount: 1,
      }),
    },
    {
      context: createMarkdownContext(1, {
        kind: "table",
        parent: 0,
        firstChild: 2,
        nextSibling: 6,
      }),
    },
    {
      context: createMarkdownContext(2, {
        kind: "row",
        parent: 1,
        firstChild: 3,
        nextSibling: 4,
        header: true,
      }),
    },
    { context: createMarkdownContext(3, { kind: "cell", parent: 2, header: true, headerCell: 3 }) },
    { context: createMarkdownContext(4, { kind: "row", parent: 1, firstChild: 5 }) },
    { context: createMarkdownContext(5, { kind: "cell", parent: 4, headerCell: 3 }) },
    {
      context: createMarkdownContext(6, {
        kind: "table",
        parent: 0,
        firstChild: 7,
        firstPiece: 0,
        pieceCount: 1,
      }),
    },
    {
      context: createMarkdownContext(7, {
        kind: "row",
        parent: 6,
        firstChild: 8,
        firstPiece: 0,
        pieceCount: 1,
      }),
    },
    {
      context: createMarkdownContext(8, {
        kind: "cell",
        parent: 7,
        firstPiece: 0,
        pieceCount: 1,
        headerCell: 3,
      }),
    },
  ];
  const source = new SyntheticMarkdownPageSource([createMarkdownPiece(0, 8, "body")], contexts);
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()), /different table/u);
  assertUncommitted(assembler, 0);
});

void test("keeps UTF-8 source ranges opaque and detaches page records", async () => {
  const text = "A😀";
  const contextRange = { start: 0, end: new TextEncoder().encode(text).length };
  const pieceRange = { start: 1, end: new TextEncoder().encode(text).length };
  const root = createMarkdownContext(0, {
    kind: "document",
    firstChild: 1,
    firstPiece: 0,
    pieceCount: 1,
    source: contextRange,
  });
  const paragraph = createMarkdownContext(1, {
    parent: 0,
    firstPiece: 0,
    pieceCount: 1,
    source: contextRange,
  });
  const piece = createMarkdownPiece(0, 1, "😀", { source: pieceRange });
  const source = new SyntheticMarkdownPageSource(
    [piece],
    [{ context: root }, { context: paragraph }],
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  const snapshot = await assembler.append(signal());
  contextRange.end = 2;
  pieceRange.start = 0;

  assert.equal(text.length, 3);
  assert.equal(snapshot.contexts.get(1)?.context.source.end, 5);
  assert.deepEqual(snapshot.pieces.get(0)?.source, { start: 1, end: 5 });
});

void test("rejects a context descriptor that changes between value pages", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "text")],
    [
      {
        context: createMarkdownContext(0, {
          kind: "document",
          firstChild: 1,
          firstPiece: 0,
          pieceCount: 1,
        }),
      },
      {
        context: createMarkdownContext(1, {
          parent: 0,
          firstPiece: 0,
          pieceCount: 1,
          valueCount: 2,
        }),
        values: [
          createMarkdownValue("destination", "/first/", 0, true),
          createMarkdownValue("destination", "last", 1),
        ],
      },
    ],
    {
      valuePageSize: 1,
      transformContextPage: (page) =>
        page.from === 1 ? { ...page, context: { ...page.context, childCount: 1 } } : page,
    },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  await assert.rejects(assembler.append(signal()), /context changed/u);
  assertUncommitted(assembler, 0);
});

void test("allows only safe absolute and relative Markdown links", () => {
  for (const destination of [
    "https://example.test/path",
    "HTTP://example.test/path",
    "mailto:user@example.test",
    "relative/path?query=1#section",
    "/root/path",
    "?query=1",
    "#section",
  ])
    assert.equal(safeMarkdownLink(destination), destination);

  for (const destination of [
    undefined,
    "javascript:alert(1)",
    "data:text/html,unsafe",
    "ftp://example.test/file",
    "//example.test/path",
    "https:\\\\example.test/path",
    "https://example.test/line\nbreak",
    "https://example.test/\u0000",
    "https://example.test/\u0085",
  ])
    assert.equal(safeMarkdownLink(destination), undefined);
});

void test("accepts an empty terminal piece page", async () => {
  const source = new SyntheticMarkdownPageSource([], [], { pieceCount: 7 });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 7);

  const snapshot = await assembler.append(signal());

  assert.equal(snapshot.from, 7);
  assert.equal(snapshot.next, 7);
  assert.equal(snapshot.hasMore, false);
  assert.equal(snapshot.pieces.size, 0);
});
