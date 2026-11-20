import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownWindowAssembler } from "../../../markdown-rendering/windowing/MarkdownWindowAssembler.ts";
import type { MarkdownPiecePage } from "../../../markdown-rendering/MarkdownRenderModel.ts";
import { createMarkdownPiece, SyntheticMarkdownPageSource } from "../MarkdownRenderFixture.ts";
import {
  signal,
  assertWindow,
  assertIds,
  trim,
  linearSource,
  destinationParts,
} from "./MarkdownWindowFixtures.ts";

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

void test("accepts an empty terminal piece page", async () => {
  const source = new SyntheticMarkdownPageSource([], [], { pieceCount: 7 });
  const assembler = new MarkdownWindowAssembler(source, source.manifest, 7);

  const snapshot = await assembler.append(signal());

  assertWindow(snapshot, { from: 7, next: 7, hasMore: false });
  assert.equal(snapshot.pieces.size, 0);
});
