import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownWindowAssembler } from "../../../markdown-rendering/windowing/MarkdownWindowAssembler.ts";
import {
  createMarkdownPiece,
  createMarkdownValue,
  SyntheticMarkdownPageSource,
  type MarkdownRenderFixtureOptions,
} from "../MarkdownRenderFixture.ts";
import {
  signal,
  assertWindow,
  assertIds,
  assertAppendRejected,
  textSource,
  tableSource,
} from "./MarkdownWindowFixtures.ts";

void test("uses bundled descriptors on append and prepend without descriptor reads", async () => {
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(1, 2, "second")],
    [
      { kind: "document", children: [1, 2], pieces: [0, 2] },
      { parent: 0, nextSibling: 2, pieces: [0, 1] },
      { parent: 0, pieces: [1, 1] },
    ],
    {
      piecePageSize: 1,
      transformPiecePage: (page) => ({
        ...page,
        contexts: [source.contexts[2]!.context, source.contexts[0]!.context],
      }),
      transformBeforePiecePage: (page) => ({
        ...page,
        contexts: [source.contexts[1]!.context, source.contexts[0]!.context],
      }),
    },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 1);

  assertIds((await assembler.append(signal())).contexts, [0, 2]);
  const snapshot = await assembler.prepend(signal());
  assertWindow(snapshot, { from: 0, next: 2, hasMore: false });
  assertIds(snapshot.contexts, [0, 1, 2]);
  assertIds(snapshot.pieces, [0, 1]);
  assert.deepEqual(source.contextDescriptorRequests, []);
  assert.deepEqual(source.contextRequests, []);
});

void test("follows partial bundled descriptors to missing ancestry and table dependencies", async () => {
  const source = tableSource({
    piecePageSize: 1,
    valuePageSize: 1,
    transformPiecePage: (page) => ({
      ...page,
      contexts: [6, 5, 3, 2, 1].map((id) => source.contexts[id]!.context),
    }),
  });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 2);

  const snapshot = await assembler.append(signal());

  assertIds(snapshot.contexts, [0, 1, 2, 3, 4, 5, 6]);
  assert.deepEqual(snapshot.displayRanges, [{ from: 2, next: 3 }]);
  assert.deepEqual(snapshot.supportRanges, [{ from: 0, next: 2 }]);
  assert.equal(snapshot.contexts.get(3)?.values.title, "column name");
  assert.deepEqual(
    source.contextDescriptorRequests.flatMap(({ ids }) => ids).sort((a, b) => a - b),
    [0, 4],
  );
  assert.deepEqual(
    source.contextRequests.map(({ id }) => id),
    [3, 3],
  );
});

void test("uses detached page hints only for header content, not subsequent rows", async () => {
  const source = new SyntheticMarkdownPageSource(
    [
      createMarkdownPiece(0, 4, "header"),
      createMarkdownPiece(1, 6, "skipped body"),
      createMarkdownPiece(2, 8, "body"),
    ],
    [
      { kind: "document", children: [1, 1], pieces: [0, 3] },
      { kind: "table", parent: 0, children: [2, 3], pieces: [0, 3] },
      { kind: "row", parent: 1, children: [3, 1], nextSibling: 5, pieces: [0, 1], header: true },
      { kind: "cell", parent: 2, children: [4, 1], pieces: [0, 1], header: true, headerCell: 3 },
      { kind: "strong", parent: 3, pieces: [0, 1] },
      { kind: "row", parent: 1, children: [6, 1], nextSibling: 7, pieces: [1, 1] },
      { kind: "cell", parent: 5, pieces: [1, 1], headerCell: 3 },
      { kind: "row", parent: 1, children: [8, 1], pieces: [2, 1] },
      { kind: "cell", parent: 7, pieces: [2, 1], headerCell: 3 },
    ],
    {
      piecePageSize: 2,
      transformPiecePage: (page) => ({
        ...page,
        contexts: (page.from === 0 ? [4, 3] : [8, 7, 3, 2, 1, 0]).map(
          (id) => source.contexts[id]!.context,
        ),
      }),
    },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 2);

  const snapshot = await assembler.append(signal());

  assert.equal(snapshot.contexts.get(4)?.context.kind, "strong");
  assertIds(snapshot.contexts, [0, 1, 2, 3, 4, 7, 8]);
  assertIds(snapshot.pieces, [0, 2]);
  assert.deepEqual(snapshot.displayRanges, [{ from: 2, next: 3 }]);
  assert.deepEqual(snapshot.supportRanges, [{ from: 0, next: 1 }]);
  assert.deepEqual(
    source.pieceRequests.map(({ index }) => index),
    [2, 0],
  );
  assert.deepEqual(source.contextDescriptorRequests, []);
  assert.deepEqual(source.contextRequests, []);
});

void test("rejects malformed piece-page descriptors without publishing", async () => {
  const cases = [
    { contexts: null, message: /invalid Markdown.*context/iu },
    { contexts: [{ id: 2 }], message: /invalid Markdown render context/iu },
    { overrides: { kind: "unknown" }, message: /invalid Markdown render context/iu },
    { overrides: { firstValue: -1 }, message: /invalid Markdown render context/iu },
    { overrides: { values: [] }, message: /must not include values/u },
    { duplicate: true, message: /duplicate Markdown context/iu },
  ];
  for (const { contexts, overrides, duplicate, message } of cases) {
    const source = textSource(
      [
        { kind: "document", children: [1, 1], pieces: [0, 1] },
        { parent: 0, pieces: [0, 1] },
      ],
      {
        transformPiecePage: (page) => {
          const hint = { ...source.contexts[1]!.context, ...overrides };
          return {
            ...page,
            contexts: contexts !== undefined ? contexts : duplicate ? [hint, hint] : [hint],
          } as unknown as typeof page;
        },
      },
    );
    await assertAppendRejected(source, message);
    assert.deepEqual(source.contextDescriptorRequests, []);
  }
});

void test("rejects conflicts with prior or value-page descriptors atomically", async () => {
  let corruptValueDescriptor = true;
  const source = new SyntheticMarkdownPageSource(
    [createMarkdownPiece(0, 1, "first"), createMarkdownPiece(1, 2, "second")],
    [
      { kind: "document", children: [1, 2], pieces: [0, 2] },
      { parent: 0, nextSibling: 2, pieces: [0, 1] },
      {
        parent: 0,
        pieces: [1, 1],
        firstValue: 0,
        valueCount: 1,
        values: [createMarkdownValue("title", "test")],
      },
    ],
    {
      piecePageSize: 1,
      transformPiecePage: (page) => ({
        ...page,
        contexts: [source.contexts[2]!.context, source.contexts[0]!.context],
      }),
      transformBeforePiecePage: (page) => ({
        ...page,
        contexts: [{ ...source.contexts[0]!.context, loose: true }],
      }),
      transformContextPage: (page) =>
        corruptValueDescriptor ? { ...page, context: { ...page.context, firstValue: 1 } } : page,
    },
  );
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 1);
  const prior = assembler.snapshot;
  await assert.rejects(assembler.append(signal()), /context changed/u);
  assert.equal(assembler.snapshot, prior);
  corruptValueDescriptor = false;

  const next = await assembler.append(signal());
  await assert.rejects(assembler.prepend(signal()), /context changed/u);
  assert.equal(assembler.snapshot, next);
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

void test("hydrates detached table headers, cell values, and empty cells", async () => {
  const source = tableSource({ piecePageSize: 1, valuePageSize: 1 });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 2);

  const snapshot = await assembler.append(signal());

  for (const id of [0, 1, 2, 3, 4, 5, 6]) assert.ok(snapshot.contexts.has(id), `context ${id}`);
  assertIds(snapshot.pieces, [0, 1, 2]);
  assert.equal(snapshot.contexts.get(3)?.values.title, "column name");
});
