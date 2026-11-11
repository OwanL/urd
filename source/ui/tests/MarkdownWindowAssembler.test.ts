import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import type { MarkdownPiecePage } from "../markdown-rendering/MarkdownRenderModel.ts";
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

void test("hydrates descriptor seeds and dependencies in bounded paged batches", async () => {
  const count = 70;
  const source = new SyntheticMarkdownPageSource(
    Array.from({ length: count }, (_, id) => createMarkdownPiece(id, id + 1, `piece-${id}`)),
    [
      { kind: "document", children: [1, count], pieces: [0, count] },
      ...Array.from({ length: count }, (_, offset) => ({
        id: offset + 1,
        parent: 0,
        nextSibling: offset + 1 < count ? offset + 2 : -1,
        pieces: [offset, 1] as const,
      })),
    ],
    { contextDescriptorPageSize: 7 },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest);

  const snapshot = await assembler.append(signal());

  assert.equal(snapshot.contexts.size, count + 1);
  assert.equal(source.contextRequests.length, 0, "zero-value descriptors need no value reads");
  assert.ok(source.contextDescriptorRequests.length < count + 1);
  assert.ok(source.contextDescriptorRequests.every(({ ids }) => ids.length <= 32));
  assert.deepEqual(
    [...new Set(source.contextDescriptorRequests.map(({ ids }) => ids.join(",")))].map(
      (ids) => ids.split(",").length,
    ),
    [32, 32, 6, 1],
  );
  for (const batch of new Set(source.contextDescriptorRequests.map(({ ids }) => ids.join(",")))) {
    const pages = source.contextDescriptorRequests.filter(({ ids }) => ids.join(",") === batch);
    assert.deepEqual(
      pages.map(({ cursor }) => cursor.index),
      Array.from({ length: Math.ceil(batch.split(",").length / 7) }, (_, index) => index * 7),
    );
  }

  const siblingSource = textSource([
    { kind: "document", children: [1, 2], pieces: [0, 1] },
    { parent: 0, nextSibling: 2, pieces: [0, 1] },
    { parent: 0, pieces: [0, 0] },
  ]);
  const siblingSnapshot = await new MarkdownWindowAssembler(
    siblingSource,
    siblingSource.manifest,
  ).append(signal());
  assertIds(siblingSnapshot.contexts, [0, 1]);
  assert.ok(siblingSource.contextDescriptorRequests.every(({ ids }) => !ids.includes(2)));
});

void test("rejects malformed or stale descriptor batches without publishing", async () => {
  const cases: Array<{ options: MarkdownRenderFixtureOptions; message: RegExp }> = [
    {
      options: { contextDescriptorGeneration: "stale-generation" },
      message: /generation changed/u,
    },
    {
      options: {
        transformContextDescriptorPage: (page) => ({
          ...page,
          contexts: [],
          next: page.from,
          hasMore: true,
        }),
      },
      message: /descriptor page/u,
    },
    {
      options: {
        transformContextDescriptorPage: (page) => ({ ...page, next: page.next - 1 }),
      },
      message: /descriptor page/u,
    },
    {
      options: {
        transformContextDescriptorPage: (page) => ({
          ...page,
          contexts: page.contexts.map((context) => ({ ...context, id: 0 })),
        }),
      },
      message: /Invalid Markdown render context/u,
    },
    {
      options: {
        transformContextDescriptorPage: (page) => ({
          ...page,
          values: [createMarkdownValue("title", "extra")],
        }),
      },
      message: /must not include values/u,
    },
  ];

  for (const { options, message } of cases) {
    const source = textSource(
      [
        { kind: "document", children: [1, 1], pieces: [0, 1] },
        { parent: 0, pieces: [0, 1] },
      ],
      options,
    );
    await assertAppendRejected(source, message);
  }

  const failureTransforms = [
    (
      page: Parameters<
        NonNullable<MarkdownRenderFixtureOptions["transformContextDescriptorPage"]>
      >[0],
    ) => (page.contexts[0]?.id === 2 ? { ...page, next: page.next - 1 } : page),
    (
      page: Parameters<
        NonNullable<MarkdownRenderFixtureOptions["transformContextDescriptorPage"]>
      >[0],
    ) => (page.contexts[0]?.id === 2 ? { ...page, generation: "stale-generation" } : page),
  ];
  for (const transformContextDescriptorPage of failureTransforms) {
    const source = new SyntheticMarkdownPageSource(
      [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(1, 2, "second")],
      [
        { kind: "document", children: [1, 2], pieces: [0, 2] },
        { parent: 0, pieces: [0, 1] },
        { parent: 0, pieces: [1, 1] },
      ],
      { piecePageSize: 1, transformContextDescriptorPage },
    );
    const assembler = new MarkdownWindowAssembler(source, source.manifest);
    const prior = await assembler.append(signal());
    await assert.rejects(assembler.append(signal()));
    assert.equal(assembler.snapshot, prior);
    assertIds(prior.contexts, [0, 1]);
  }
});

void test("aborted descriptor batches leave the committed snapshot usable", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(1, 2, "second")],
    [
      { kind: "document", children: [1, 2], pieces: [0, 2] },
      { parent: 0, pieces: [0, 1] },
      { parent: 0, pieces: [1, 1] },
    ],
    { piecePageSize: 1 },
  );
  let release!: () => void;
  let entered!: () => void;
  let holdBatch = false;
  const gate = new Promise<void>((resolve) => (release = resolve));
  const started = new Promise<void>((resolve) => (entered = resolve));
  const heldSource = {
    manifest: source.manifest,
    readPieces: source.readPieces.bind(source),
    readPiecesBefore: source.readPiecesBefore.bind(source),
    readContext: source.readContext.bind(source),
    async readContexts(
      ids: Parameters<typeof source.readContexts>[0],
      cursor: Parameters<typeof source.readContexts>[1],
      signal: AbortSignal,
    ) {
      if (holdBatch) {
        holdBatch = false;
        entered();
        await gate;
      }
      return source.readContexts(ids, cursor, signal);
    },
    locate: source.locate.bind(source),
    locateChild: source.locateChild.bind(source),
  };
  const assembler = new MarkdownWindowAssembler(heldSource, source.manifest);
  const prior = await assembler.append(signal());
  assertWindow(prior, { next: 1 });
  holdBatch = true;

  const controller = new AbortController();
  const pending = assembler.append(controller.signal);
  await started;
  controller.abort();
  release();
  await assert.rejects(pending);
  assert.equal(assembler.snapshot, prior);

  const next = await assembler.append(signal());
  assertWindow(next, { next: 2, hasMore: false });
  assert.equal(next.contexts.get(2)?.context.parent, 0);
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
    readContexts: source.readContexts.bind(source),
    locate: source.locate.bind(source),
    locateChild: source.locateChild.bind(source),
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

void test("rejects malformed append pages without committing", async () => {
  const otherTable = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 8, "body")],
    [
      { kind: "document", children: [1, 0], pieces: [0, 1] },
      { kind: "table", parent: 0, children: [2, 0], nextSibling: 6 },
      { kind: "row", parent: 1, children: [3, 0], nextSibling: 4, header: true },
      { kind: "cell", parent: 2, header: true, headerCell: 3 },
      { kind: "row", parent: 1, children: [5, 0] },
      { kind: "cell", parent: 4, headerCell: 3 },
      { kind: "table", parent: 0, children: [7, 0], pieces: [0, 1] },
      { kind: "row", parent: 6, children: [8, 0], pieces: [0, 1] },
      { kind: "cell", parent: 7, pieces: [0, 1], headerCell: 3 },
    ],
  );
  const incompletePiece = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "continued", { part: 1 })],
    [],
    { contextCount: 2 },
  );
  const cases: Array<{ source: SyntheticMarkdownPageSource; message?: RegExp }> = [
    {
      source: textSource([{ id: 1, pieces: [0, 1] }], { pieceGeneration: "stale-generation" }),
    },
    {
      source: valueSource([createMarkdownValue("destination", "/target")], {
        contextGeneration: "stale-generation",
      }),
    },
    {
      source: new SyntheticMarkdownPageSource([], [], {
        pieceCount: 1,
        transformPiecePage: (page) => ({ ...page, next: page.from, hasMore: true }),
      }),
    },
    {
      source: valueSource(destinationParts("/first/", "last"), {
        valuePageSize: 1,
        transformContextPage: (page) => ({ ...page, next: page.from, hasMore: true }),
      }),
    },
    {
      source: linearSource(
        [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(2, 1, "skipped")],
        { transformPiecePage: (page) => ({ ...page, next: page.from + page.pieces.length }) },
      ),
    },
    { source: valueSource(destinationParts("/first/", "last", 2), { valuePageSize: 1 }) },
    { source: otherTable, message: /different table/u },
    { source: incompletePiece, message: /incomplete text piece/u },
    {
      source: valueSource(destinationParts("/first/", "last"), {
        valuePageSize: 1,
        transformContextPage: (page) =>
          page.from === 1 ? { ...page, context: { ...page.context, childCount: 1 } } : page,
      }),
      message: /context changed/u,
    },
  ];

  for (const { source, message } of cases) await assertAppendRejected(source, message);
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
