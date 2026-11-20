import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownWindowAssembler } from "../../../markdown-rendering/windowing/MarkdownWindowAssembler.ts";
import { createMarkdownPiece } from "../MarkdownRenderFixture.ts";
import {
  signal,
  assertWindow,
  assertIds,
  trim,
  linearSource,
  tableSource,
} from "./MarkdownWindowFixtures.ts";

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
