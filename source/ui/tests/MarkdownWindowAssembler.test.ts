import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import type { MarkdownPiecePage } from "../markdown-rendering/MarkdownRenderModel.ts";
import { safeMarkdownLink } from "../markdown-rendering/MarkdownLinkPolicy.ts";
import {
  createMarkdownPiece,
  createMarkdownValue,
  SyntheticMarkdownPageSource,
  type MarkdownContextDescriptor,
  type MarkdownRenderFixtureOptions,
} from "./MarkdownRenderFixture.ts";

function signal() {
  return new AbortController().signal;
}

function assertWindow(
  snapshot: MarkdownWindowAssembler["snapshot"],
  expected: { from?: number; next?: number; hasMore?: boolean },
) {
  if (expected.from !== undefined) assert.equal(snapshot.from, expected.from);
  if (expected.next !== undefined) assert.equal(snapshot.next, expected.next);
  if (expected.hasMore !== undefined) assert.equal(snapshot.hasMore, expected.hasMore);
}

function assertIds(map: ReadonlyMap<number, unknown>, ids: number[]) {
  assert.deepEqual(
    [...map.keys()].sort((left, right) => left - right),
    ids,
  );
}

async function assertAppendRejected(
  source: SyntheticMarkdownPageSource,
  message: RegExp = /Markdown/u,
): Promise<void> {
  const assembler = new MarkdownWindowAssembler(source, source.manifest);
  await assert.rejects(assembler.append(signal()), message);
  assertWindow(assembler.snapshot, { from: 0, next: 0 });
  assert.equal(assembler.snapshot.pieces.size, 0);
  assert.equal(assembler.snapshot.contexts.size, 0);
}

type TrimRequest = Parameters<MarkdownWindowAssembler["trim"]>[0];

function trim(
  assembler: MarkdownWindowAssembler,
  active: TrimRequest["active"],
  retain?: TrimRequest["retain"],
  abort = signal(),
) {
  return assembler.trim(
    {
      generation: assembler.snapshot.generation,
      active,
      ...(retain ? { retain } : {}),
    },
    abort,
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
      { kind: "document", children: [1, 1], pieces: [0, pieceCount] },
      { parent: 0, pieces: [0, pieceCount] },
    ],
    options,
  );
}

function destinationParts(first: string, last: string, lastPart = 1) {
  return [
    createMarkdownValue("destination", first, 0, true),
    createMarkdownValue("destination", last, lastPart),
  ];
}

function textSource(
  contexts: readonly MarkdownContextDescriptor[],
  options: MarkdownRenderFixtureOptions = {},
) {
  return new SyntheticMarkdownPageSource([createMarkdownPiece(0, 1, "text")], contexts, options);
}

function valueSource(
  values: ReturnType<typeof createMarkdownValue>[],
  options: MarkdownRenderFixtureOptions = {},
) {
  return textSource(
    [
      { kind: "document", children: [1, 1], pieces: [0, 1] },
      { parent: 0, pieces: [0, 1], valueCount: values.length, values },
    ],
    options,
  );
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
      { kind: "document", children: [1, 2], pieces: [0, 7] },
      { kind: "table", parent: 0, children: [2, 2], nextSibling: 7, pieces: [0, 6] },
      { kind: "row", parent: 1, children: [3, 2], nextSibling: 5, pieces: [0, 2], header: true },
      {
        kind: "cell",
        parent: 2,
        nextSibling: 4,
        pieces: [0, 2],
        valueCount: 2,
        header: true,
        headerCell: 3,
        values: [
          createMarkdownValue("title", "column ", 0, true),
          createMarkdownValue("title", "name", 1),
        ],
      },
      { kind: "cell", parent: 2, pieces: [2, 0], header: true, headerCell: 4 },
      { kind: "row", parent: 1, children: [6, 1], pieces: [2, 4] },
      { kind: "cell", parent: 5, pieces: [2, 4], headerCell: 3 },
      { parent: 0, pieces: [6, 1] },
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
      { kind: "document", children: [1, 0], pieces: [0, 2] },
      { parent: 0, pieces: [0, 2], valueCount: 2, values: destinationParts("/example/", "target") },
    ],
    { piecePageSize: 1, valuePageSize: 1 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  const first = await assembler.append(signal());
  assertWindow(first, { next: 1, hasMore: true });
  assert.deepEqual([...first.pieces.keys()], [0]);

  const second = await assembler.append(signal());
  assertWindow(second, { next: 2, hasMore: false });
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
      { kind: "document", children: [1, 0], pieces: [3, 2] },
      { parent: 0, pieces: [3, 2] },
    ],
    { piecePageSize: 1 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 3);

  const middle = await assembler.append(signal());
  assertWindow(middle, { from: 3, next: 4 });
  assert.deepEqual([...middle.pieces.keys()], [3]);

  const end = await assembler.append(signal());
  assertWindow(end, { next: 5, hasMore: false });
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
  assertWindow(initial, { from: 3, next: 5, hasMore: false });

  const last = await assembler.prepend(signal());
  assertWindow(last, { from: 2, next: 5, hasMore: false });
  const middle = await assembler.prepend(signal());
  const first = await assembler.prepend(signal());
  assert.equal(middle.from, 1);
  assertWindow(first, { from: 0 });
  assertIds(first.pieces, [0, 1, 2, 3, 4]);
  assert.deepEqual(
    source.beforePieceRequests.map((cursor) => cursor.index),
    [3, 2, 1],
  );
  assert.equal(await assembler.prepend(signal()), first);
});

void test("failed prepend leaves its prior snapshot usable", async () => {
  const cases = [
    ["stale generation", { beforePieceGeneration: "stale-generation" }, /generation changed/u],
    [
      "malformed cursor",
      {
        transformBeforePiecePage: (page: MarkdownPiecePage) => ({ ...page, next: page.next - 1 }),
      },
      /preceding page/u,
    ],
    ["continuation seam", {}, /continuation is discontinuous/u],
  ] as const;

  for (const [name, options, message] of cases) {
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
  assertIds(snapshot.pieces, [0, 1, 2]);
  assert.equal(snapshot.contexts.get(3)?.values.title, "column name");
});

void test("rejects stale-generation and nonadvancing pages without committing", async () => {
  const cases = [
    textSource([{ id: 1, pieces: [0, 1] }], {
      pieceGeneration: "stale-generation",
    }),
    valueSource([createMarkdownValue("destination", "/target")], {
      contextGeneration: "stale-generation",
    }),
    new SyntheticMarkdownPageSource([], [], {
      pieceCount: 1,
      transformPiecePage: (page) => ({ ...page, next: page.from, hasMore: true }),
    }),
    valueSource(destinationParts("/first/", "last"), {
      valuePageSize: 1,
      transformContextPage: (page) => ({ ...page, next: page.from, hasMore: true }),
    }),
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
  const source = valueSource(destinationParts("/first/", "last", 2), { valuePageSize: 1 });
  await assertAppendRejected(source);
});

void test("rejects a header reference from another table", async () => {
  const contexts: MarkdownContextDescriptor[] = [
    { kind: "document", children: [1, 0], pieces: [0, 1] },
    { kind: "table", parent: 0, children: [2, 0], nextSibling: 6 },
    { kind: "row", parent: 1, children: [3, 0], nextSibling: 4, header: true },
    { kind: "cell", parent: 2, header: true, headerCell: 3 },
    { kind: "row", parent: 1, children: [5, 0] },
    { kind: "cell", parent: 4, headerCell: 3 },
    { kind: "table", parent: 0, children: [7, 0], pieces: [0, 1] },
    { kind: "row", parent: 6, children: [8, 0], pieces: [0, 1] },
    { kind: "cell", parent: 7, pieces: [0, 1], headerCell: 3 },
  ];
  const source = new SyntheticMarkdownPageSource([createMarkdownPiece(0, 8, "body")], contexts);
  await assertAppendRejected(source, /different table/u);
});

void test("keeps UTF-8 source ranges opaque and detaches page records", async () => {
  const text = "A😀";
  const contextRange = { start: 0, end: new TextEncoder().encode(text).length };
  const pieceRange = { start: 1, end: new TextEncoder().encode(text).length };
  const piece = createMarkdownPiece(0, 1, "😀", { source: pieceRange });
  const source = new SyntheticMarkdownPageSource(
    [piece],
    [
      { kind: "document", children: [1, 0], pieces: [0, 1], source: contextRange },
      { parent: 0, pieces: [0, 1], source: contextRange },
    ],
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
  const source = valueSource(destinationParts("/first/", "last"), {
    valuePageSize: 1,
    transformContextPage: (page) =>
      page.from === 1 ? { ...page, context: { ...page.context, childCount: 1 } } : page,
  });
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

  assertWindow(snapshot, { from: 4, next: 7 });
  assert.deepEqual(snapshot.displayRanges, [
    { from: 0, next: 7 },
    { from: 9, next: 12 },
  ]);
  assert.deepEqual(snapshot.supportRanges, []);
  assertIds(snapshot.pieces, [0, 1, 2, 3, 4, 5, 6, 9, 10, 11]);
  assertIds(snapshot.contexts, [0, 1]);
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

  const generation = source.manifest.generation;
  const active = { from: 1, next: 2 };
  for (const request of [
    { generation: "stale-generation", active: { from: 0, next: 1 } },
    { generation, active: { from: 3, next: 4 } },
    { generation, active, retain: [{ from: 2, next: 4 }] },
    { generation, active, retain: [{ from: 3, next: 2 }] },
  ])
    await rejected(request);
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(trim(assembler, active, undefined, controller.signal));
  assert.equal(assembler.snapshot, prior);
  const during = new AbortController();
  const pending = trim(assembler, active, undefined, during.signal);
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
  assertWindow(body, { from: 5, next: 7 });
  assert.deepEqual(body.displayRanges, [{ from: 5, next: 7 }]);
  assert.deepEqual(body.supportRanges, [{ from: 0, next: 2 }]);
  for (const id of [1, 2, 3, 4, 5, 6]) assert.ok(body.contexts.has(id), `context ${id}`);
  assertIds(body.pieces, [0, 1, 5, 6]);
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
  assertIds(reclaimed.contexts, [0, 7]);

  const previousHeaderReads = source.pieceRequests.filter((request) => request.index === 0).length;
  const rehydrated = await assembler.prepend(signal());
  assertWindow(rehydrated, { from: 5 });
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
  assertWindow(appended, { from: 3, next: 7 });
  const prepended = await assembler.prepend(signal());
  assertWindow(prepended, { from: 1, next: 7 });
  assertIds(prepended.pieces, [1, 2, 3, 4, 5, 6]);
});

void test("accepts an empty terminal piece page", async () => {
  const source = new SyntheticMarkdownPageSource([], [], { pieceCount: 7 });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 7);

  const snapshot = await assembler.append(signal());

  assertWindow(snapshot, { from: 7, next: 7, hasMore: false });
  assert.equal(snapshot.pieces.size, 0);
});
