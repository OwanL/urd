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

async function assertAppendRejected(
  source: SyntheticMarkdownPageSource,
  message: RegExp = /Markdown/u,
): Promise<void> {
  const assembler = new MarkdownWindowAssembler(source, source.manifest);
  await assert.rejects(assembler.append(signal()), message);
  assertUncommitted(assembler, 0);
}

type TrimRequest = Parameters<MarkdownWindowAssembler["trim"]>[0];

function trim(
  assembler: MarkdownWindowAssembler,
  active: TrimRequest["active"],
  retain?: TrimRequest["retain"],
) {
  return assembler.trim(
    {
      generation: assembler.snapshot.generation,
      active,
      ...(retain ? { retain } : {}),
    },
    signal(),
  );
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

function contextEntry(id: number, overrides: Parameters<typeof createMarkdownContext>[1]) {
  return { context: createMarkdownContext(id, overrides) };
}

function tableSource(options: MarkdownRenderFixtureOptions = {}) {
  return new SyntheticMarkdownPageSource(
    [
      createMarkdownPiece(0, 3, "header"),
      createMarkdownPiece(1, 3, ""),
      ...Array.from({ length: 4 }, (_, offset) =>
        createMarkdownPiece(offset + 2, 6, `body-${offset}`),
      ),
      createMarkdownPiece(6, 7, "outside"),
    ],
    [
      contextEntry(0, {
        kind: "document",
        firstChild: 1,
        childCount: 2,
        firstPiece: 0,
        pieceCount: 7,
      }),
      contextEntry(1, {
        kind: "table",
        parent: 0,
        firstChild: 2,
        nextSibling: 7,
        childCount: 2,
        firstPiece: 0,
        pieceCount: 6,
      }),
      contextEntry(2, {
        kind: "row",
        parent: 1,
        firstChild: 3,
        nextSibling: 5,
        childCount: 2,
        firstPiece: 0,
        pieceCount: 2,
        header: true,
      }),
      {
        ...contextEntry(3, {
          kind: "cell",
          parent: 2,
          nextSibling: 4,
          firstPiece: 0,
          pieceCount: 2,
          valueCount: 2,
          header: true,
          headerCell: 3,
        }),
        values: [
          createMarkdownValue("title", "column ", 0, true),
          createMarkdownValue("title", "name", 1),
        ],
      },
      contextEntry(4, {
        kind: "cell",
        parent: 2,
        firstPiece: 2,
        header: true,
        headerCell: 4,
      }),
      contextEntry(5, {
        kind: "row",
        parent: 1,
        firstChild: 6,
        childCount: 1,
        firstPiece: 2,
        pieceCount: 4,
      }),
      contextEntry(6, {
        kind: "cell",
        parent: 5,
        firstPiece: 2,
        pieceCount: 4,
        headerCell: 3,
      }),
      contextEntry(7, { parent: 0, firstPiece: 6, pieceCount: 1 }),
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
      contextEntry(0, { kind: "document", firstChild: 1, firstPiece: 0, pieceCount: 2 }),
      {
        ...contextEntry(1, { parent: 0, firstPiece: 0, pieceCount: 2, valueCount: 2 }),
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
      contextEntry(0, { kind: "document", firstChild: 1, firstPiece: 3, pieceCount: 2 }),
      contextEntry(1, { parent: 0, firstPiece: 3, pieceCount: 2 }),
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

void test("aborted reads stay atomic and release the shared read lock", async () => {
  const source = linearSource([
    createMarkdownPiece(0, 1, "first"),
    createMarkdownPiece(1, 1, "last"),
  ]);
  let release!: () => void;
  let entered!: () => void;
  let holdRead = true;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const started = new Promise<void>((resolve) => (entered = resolve));
  const heldSource = {
    readPieces: source.readPieces.bind(source),
    async readPiecesBefore(
      cursor: Parameters<typeof source.readPiecesBefore>[0],
      signal: AbortSignal,
    ) {
      if (holdRead) {
        holdRead = false;
        entered();
        await gate;
      }
      return source.readPiecesBefore(cursor, signal);
    },
    readContext: source.readContext.bind(source),
  };
  const assembler = new MarkdownWindowAssembler(heldSource, source.manifest, 1);
  await assembler.append(signal());
  const prior = assembler.snapshot;
  const controller = new AbortController();
  const pending = assembler.prepend(controller.signal);
  await started;
  await assert.rejects(assembler.append(signal()), /already pending/u);
  await assert.rejects(trim(assembler, { from: 1, next: 2 }), /already pending/u);
  controller.abort();
  release();

  await assert.rejects(pending);
  assert.equal(assembler.snapshot, prior);
  assert.equal((await assembler.prepend(signal())).from, 0);
  await trim(assembler, { from: 0, next: 2 });
});

void test("hydrates detached table headers, cell values, and empty cells", async () => {
  const source = tableSource({ piecePageSize: 1, valuePageSize: 1 });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 2);

  const snapshot = await assembler.append(signal());

  for (const id of [0, 1, 2, 3, 4, 5, 6]) assert.ok(snapshot.contexts.has(id), `context ${id}`);
  assert.deepEqual(
    [...snapshot.pieces.keys()].sort((left, right) => left - right),
    [0, 1, 2],
  );
  assert.equal(snapshot.contexts.get(3)?.values.title, "column name");
});

void test("rejects stale-generation and nonadvancing pages without committing", async () => {
  const cases = [
    new SyntheticMarkdownPageSource(
      [createMarkdownPiece(0, 1, "text")],
      [{ context: createMarkdownContext(1, { firstPiece: 0, pieceCount: 1 }) }],
      { pieceGeneration: "stale-generation" },
    ),
    new SyntheticMarkdownPageSource(
      [createMarkdownPiece(0, 1, "text")],
      [
        {
          context: createMarkdownContext(1, { firstPiece: 0, pieceCount: 1, valueCount: 1 }),
          values: [createMarkdownValue("destination", "/target")],
        },
      ],
      { contextGeneration: "stale-generation" },
    ),
    new SyntheticMarkdownPageSource([], [], {
      pieceCount: 1,
      transformPiecePage: (page) => ({ ...page, next: page.from, hasMore: true }),
    }),
    new SyntheticMarkdownPageSource(
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
    ),
  ];

  for (const source of cases) await assertAppendRejected(source);
});

void test("rejects skipped piece IDs without committing", async () => {
  const source = linearSource(
    [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(2, 1, "skipped")],
    { transformPiecePage: (page) => ({ ...page, next: page.from + page.pieces.length }) },
  );
  await assertAppendRejected(source);
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
  await assertAppendRejected(source);
});

void test("rejects a header reference from another table", async () => {
  const contexts = [
    contextEntry(0, { kind: "document", firstChild: 1, firstPiece: 0, pieceCount: 1 }),
    contextEntry(1, { kind: "table", parent: 0, firstChild: 2, nextSibling: 6 }),
    contextEntry(2, { kind: "row", parent: 1, firstChild: 3, nextSibling: 4, header: true }),
    contextEntry(3, { kind: "cell", parent: 2, header: true, headerCell: 3 }),
    contextEntry(4, { kind: "row", parent: 1, firstChild: 5 }),
    contextEntry(5, { kind: "cell", parent: 4, headerCell: 3 }),
    contextEntry(6, {
      kind: "table",
      parent: 0,
      firstChild: 7,
      firstPiece: 0,
      pieceCount: 1,
    }),
    contextEntry(7, { kind: "row", parent: 6, firstChild: 8, firstPiece: 0, pieceCount: 1 }),
    contextEntry(8, {
      kind: "cell",
      parent: 7,
      firstPiece: 0,
      pieceCount: 1,
      headerCell: 3,
    }),
  ];
  const source = new SyntheticMarkdownPageSource([createMarkdownPiece(0, 8, "body")], contexts);
  await assertAppendRejected(source, /different table/u);
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
  await assertAppendRejected(source, /context changed/u);
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

void test("trims to normalized disjoint display ranges while preserving the active window", async () => {
  const source = linearSource(
    Array.from({ length: 12 }, (_, id) => createMarkdownPiece(id, 1, `piece-${id}`)),
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);
  await assembler.append(signal());

  const snapshot = await trim(assembler, { from: 4, next: 7 }, [
    { from: 0, next: 3 },
    { from: 2, next: 5 },
    { from: 9, next: 11 },
    { from: 10, next: 12 },
  ]);

  assert.equal(snapshot.from, 4);
  assert.equal(snapshot.next, 7);
  assert.deepEqual(snapshot.displayRanges, [
    { from: 0, next: 7 },
    { from: 9, next: 12 },
  ]);
  assert.deepEqual(snapshot.supportRanges, []);
  assert.deepEqual(
    [...snapshot.pieces.keys()].sort((left, right) => left - right),
    [0, 1, 2, 3, 4, 5, 6, 9, 10, 11],
  );
  assert.deepEqual(
    [...snapshot.contexts.keys()].sort((left, right) => left - right),
    [0, 1],
  );
  // Both endpoints exist, but an interior hole must not silently lose selected text.
  await assert.rejects(
    trim(assembler, { from: 4, next: 7 }, [{ from: 6, next: 10 }]),
    /incomplete/u,
  );
  assert.equal(assembler.snapshot, snapshot);
});

void test("rejects stale, invalid, or incomplete trims without committing", async () => {
  const source = linearSource(
    Array.from({ length: 4 }, (_, id) => createMarkdownPiece(id, 1, `piece-${id}`)),
    { piecePageSize: 3 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);
  const prior = await assembler.append(signal());
  const rejected = async (request: Parameters<typeof assembler.trim>[0]) => {
    await assert.rejects(assembler.trim(request, signal()));
    assert.equal(assembler.snapshot, prior);
  };

  await rejected({
    generation: "stale-generation",
    active: { from: 0, next: 1 },
  });
  await rejected({
    generation: source.manifest.generation,
    active: { from: 3, next: 4 },
  });
  await rejected({
    generation: source.manifest.generation,
    active: { from: 1, next: 2 },
    retain: [{ from: 2, next: 4 }],
  });
  await rejected({
    generation: source.manifest.generation,
    active: { from: 1, next: 2 },
    retain: [{ from: 3, next: 2 }],
  });
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(
    assembler.trim(
      { generation: source.manifest.generation, active: { from: 1, next: 2 } },
      controller.signal,
    ),
  );
  assert.equal(assembler.snapshot, prior);
  const during = new AbortController();
  const pending = assembler.trim(
    { generation: prior.generation, active: { from: 1, next: 2 } },
    during.signal,
  );
  during.abort();
  await assert.rejects(pending);
  assert.equal(assembler.snapshot, prior);
  assert.equal((await trim(assembler, { from: 1, next: 2 })).pieces.size, 1);
});

void test("rehydrates and reclaims detached table support across prepend and trim", async () => {
  const source = tableSource({ piecePageSize: 1, valuePageSize: 1 });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 6);
  await assembler.append(signal());

  const body = await assembler.prepend(signal());
  assert.equal(body.from, 5);
  assert.equal(body.next, 7);
  assert.deepEqual(body.displayRanges, [{ from: 5, next: 7 }]);
  assert.deepEqual(body.supportRanges, [{ from: 0, next: 2 }]);
  for (const id of [1, 2, 3, 4, 5, 6]) assert.ok(body.contexts.has(id), `context ${id}`);
  assert.deepEqual(
    [...body.pieces.keys()].sort((left, right) => left - right),
    [0, 1, 5, 6],
  );
  assert.equal(body.contexts.get(3)?.values.title, "column name");
  assert.ok(source.pieceRequests.some((request) => request.index === 0));

  const requests = source.contextRequests.length;
  const selectedBody = await trim(assembler, { from: 6, next: 7 }, [{ from: 5, next: 6 }]);
  assert.deepEqual(selectedBody.supportRanges, [{ from: 0, next: 2 }]);
  assert.equal(selectedBody.contexts.get(3), body.contexts.get(3));
  assert.equal(source.contextRequests.length, requests, "Trim reuses validated loaded metadata");
  const reclaimed = await trim(assembler, { from: 6, next: 7 });
  assert.deepEqual(reclaimed.displayRanges, [{ from: 6, next: 7 }]);
  assert.deepEqual(reclaimed.supportRanges, []);
  assert.deepEqual([...reclaimed.pieces.keys()], [6]);
  assert.deepEqual(
    [...reclaimed.contexts.keys()].sort((left, right) => left - right),
    [0, 7],
  );

  const previousHeaderReads = source.pieceRequests.filter((request) => request.index === 0).length;
  const rehydrated = await assembler.prepend(signal());
  assert.equal(rehydrated.from, 5);
  assert.deepEqual(rehydrated.supportRanges, [{ from: 0, next: 2 }]);
  assert.ok(
    source.pieceRequests.filter((request) => request.index === 0).length > previousHeaderReads,
  );
});

void test("append and prepend validate continuation seams after trimming", async () => {
  const source = linearSource(
    Array.from({ length: 7 }, (_, id) =>
      createMarkdownPiece(id, 1, `part-${id}`, { part: id, continued: id < 6 }),
    ),
    { piecePageSize: 2 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 2);
  await assembler.append(signal());
  await assembler.append(signal());

  await trim(assembler, { from: 3, next: 5 });
  const appended = await assembler.append(signal());
  assert.equal(appended.from, 3);
  assert.equal(appended.next, 7);
  const prepended = await assembler.prepend(signal());
  assert.equal(prepended.from, 1);
  assert.equal(prepended.next, 7);
  assert.deepEqual(
    [...prepended.pieces.keys()].sort((left, right) => left - right),
    [1, 2, 3, 4, 5, 6],
  );
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
