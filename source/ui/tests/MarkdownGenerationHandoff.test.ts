import assert from "node:assert/strict";
import test from "node:test";
import { MarkdownGenerationHandoff } from "../markdown-rendering/MarkdownGenerationHandoff.ts";
import { MarkdownWindowAssembler } from "../markdown-rendering/MarkdownWindowAssembler.ts";
import type { MarkdownWindowAnchor } from "../markdown-rendering/MarkdownWindowGeometry.ts";
import type { MarkdownOwnedGeneration } from "../markdown-rendering/MarkdownRenderModel.ts";
import { createMarkdownPiece, SyntheticMarkdownPageSource } from "./MarkdownRenderFixture.ts";
import type { MarkdownRenderFixtureOptions } from "./MarkdownRenderFixture.ts";

const signal = () => new AbortController().signal;
const deferred = <T>() => {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => (resolve = done));
  return { promise, resolve };
};
const source = (
  generation: string,
  starts: readonly number[] = [0],
  pageSize = Infinity,
  options: MarkdownRenderFixtureOptions = {},
) => {
  const pieces = starts.map((start, id) =>
    createMarkdownPiece(id, 0, `p${id}`, { source: { start, end: start + 1 } }),
  );
  return new SyntheticMarkdownPageSource(
    pieces,
    [{ kind: "document", pieces: [0, pieces.length] }],
    {
      generation,
      sourceUtf8Bytes: Math.max(0, ...starts.map((start) => start + 1)),
      piecePageSize: pageSize,
      ...options,
    },
  );
};
function gateRead(backing: SyntheticMarkdownPageSource) {
  const started = deferred<void>(),
    gate = deferred<void>(),
    aborted = deferred<void>(),
    readDrained = deferred<void>(),
    drained = deferred<void>();
  const read = backing.readPieces.bind(backing),
    dispose = backing.dispose.bind(backing);
  let first = true;
  backing.readPieces = async (cursor, abort) => {
    if (!first) return read(cursor, abort);
    first = false;
    abort.addEventListener("abort", () => aborted.resolve(), { once: true });
    started.resolve();
    await gate.promise;
    try {
      return await read(cursor, signal());
    } finally {
      readDrained.resolve();
    }
  };
  backing.dispose = () => {
    dispose();
    drained.resolve();
  };
  return {
    source: backing,
    started: started.promise,
    aborted: aborted.promise,
    readDrained: readDrained.promise,
    release: () => gate.resolve(),
    drained: drained.promise,
  };
}
function gateDispose(backing: SyntheticMarkdownPageSource) {
  const started = deferred<void>(),
    gate = deferred<void>();
  (backing as MarkdownOwnedGeneration).dispose = async () => {
    started.resolve();
    await gate.promise;
    backing.disposals++;
  };
  return { started: started.promise, release: () => gate.resolve() };
}
const anchor = (
  generation: string,
  piece: string,
  start: string,
  offset: number,
): MarkdownWindowAnchor => ({ generation, piece, start, end: String(Number(start) + 1), offset });
const requestSource = (handoff: MarkdownGenerationHandoff, generation: string, opened = () => {}) =>
  handoff.request({
    open: async () => {
      opened();
      return source(generation);
    },
  });
async function setup(
  initial = source("old"),
  anchorValue: MarkdownWindowAnchor | null = null,
  pinned = () => false,
) {
  const initialWindow = new MarkdownWindowAssembler(initial, initial.manifest);
  await initialWindow.append(signal());
  const published: [string, MarkdownWindowAnchor | null][] = [];
  return {
    published,
    handoff: new MarkdownGenerationHandoff(initial, initialWindow, {
      capture: () => anchorValue,
      pinned,
      publish: (next, restored) => published.push([next.generation, restored]),
    }),
  };
}

void test("locate maps UTF-8 offsets to containing pieces and honors boundaries and abort", async () => {
  const page = new SyntheticMarkdownPageSource(
    [
      createMarkdownPiece(0, 0, "😀", { source: { start: 1, end: 5 } }),
      createMarkdownPiece(1, 0, "Z", { source: { start: 5, end: 6 } }),
    ],
    [{ kind: "document", pieces: [0, 2] }],
    { generation: "utf8", sourceUtf8Bytes: 6 },
  );
  for (const [offset, index] of [
    [0, 0],
    [1, 0],
    [4, 0],
    [5, 1],
    [6, 1],
  ])
    assert.equal((await page.locate(offset!, signal())).index, index);
  const abort = new AbortController();
  abort.abort();
  await assert.rejects(page.locate(0, abort.signal), { name: "AbortError" });
});

void test("keeps the old window until ready, restores its anchor, and releases it", async () => {
  const old = source("old", [0, 2, 4, 6, 8, 10]);
  const { handoff, published } = await setup(old, anchor("old", "3", "6", 17));
  await handoff.trim({ generation: "old", active: { from: 2, next: 4 } }, signal());
  const prior = handoff.snapshot,
    next = source(
      "next",
      Array.from({ length: 8 }, (_, id) => id * 2),
      3,
    );
  const gate = gateRead(next),
    release = gateDispose(old),
    publication = new AbortController(),
    pending = handoff.request({ open: async () => gate.source }, publication.signal);
  await gate.started;
  assert.equal(handoff.snapshot, prior);
  assert.equal(old.disposals, 0);
  gate.release();
  assert.equal(await pending, "published");
  await release.started;
  assert.equal(handoff.snapshot.generation, "next");
  assert.equal(handoff.snapshot.pieces.get(2)?.text, "p2");
  let opened = false;
  publication.abort();
  const queued = requestSource(handoff, "queued", () => (opened = true));
  assert.equal(opened, false);
  assert.deepEqual(
    [handoff.snapshot.from, handoff.snapshot.next, ...handoff.snapshot.pieces.keys()],
    [2, 4, 2, 3],
  );
  release.release();
  assert.equal(await queued, "published");
  assert.deepEqual(published[0], ["next", anchor("next", "3", "6", 17)]);
  assert.equal(old.disposals, 1);
});

void test("defers pinned requests and opens only the latest descriptor on resume", async () => {
  let pin: "selection" | "focus" | null = "selection",
    firstOpens = 0,
    latestOpens = 0;
  const { handoff, published } = await setup(source("old"), null, () => pin !== null);
  const first = requestSource(handoff, "first", () => firstOpens++),
    latest = requestSource(handoff, "latest", () => latestOpens++);
  assert.equal(await first, "cancelled");
  assert.equal(handoff.deferred, true);
  pin = "focus";
  handoff.resume();
  pin = null;
  handoff.resume();
  assert.equal(await latest, "published");
  assert.deepEqual([firstOpens, latestOpens, published[0]?.[0]], [0, 1, "latest"]);
});

void test("superseded opens and reads drain without publishing stale generations", async () => {
  const { handoff, published } = await setup();
  const late = deferred<MarkdownOwnedGeneration>(),
    staleOpen = source("stale-open");
  const first = handoff.request({ open: () => late.promise }),
    latest = requestSource(handoff, "latest");
  assert.equal(await first, "cancelled");
  late.resolve(staleOpen);
  assert.equal(await latest, "published");
  assert.equal(staleOpen.disposals, 1);

  const staleRead = source("stale-read"),
    gate = gateRead(staleRead);
  const reading = handoff.request({ open: async () => gate.source });
  await gate.started;
  const newest = requestSource(handoff, "newest");
  assert.equal(await reading, "cancelled");
  gate.release();
  assert.equal(await newest, "published");
  assert.deepEqual(
    [staleRead.disposals, ...published.map(([generation]) => generation)],
    [1, "latest", "newest"],
  );
});

void test("dispose waits for a gated active append before releasing its source", async () => {
  const old = source("old", [0, 1, 2], 1),
    { handoff } = await setup(old),
    gate = gateRead(old);
  const append = handoff.append(signal());
  await gate.started;
  let disposed = false;
  const disposal = handoff.dispose().then(() => (disposed = true));
  await gate.aborted;
  await Promise.resolve();
  assert.deepEqual([disposed, old.disposals], [false, 0]);
  gate.release();
  await assert.rejects(append, { name: "AbortError" });
  await disposal;
  assert.deepEqual([disposed, old.disposals], [true, 1]);
});

void test("publication quiesces active reads before releasing the old window", async () => {
  const old = source("old", [0, 1, 2], 1),
    failure = new Error("release failed"),
    { handoff, published } = await setup(old),
    gate = gateRead(old);
  (old as MarkdownOwnedGeneration).dispose = async () => {
    old.disposals++;
    throw failure;
  };
  const append = handoff.append(signal());
  await gate.started;
  const active = source("next"),
    replacement = handoff.request({ open: async () => active });
  await gate.aborted;
  assert.deepEqual([old.disposals, published.length, handoff.snapshot.generation], [0, 0, "old"]);
  await assert.rejects(handoff.append(signal()), { name: "AbortError" });
  gate.release();
  await gate.readDrained;
  assert.equal(old.disposals, 0);
  await assert.rejects(append, { name: "AbortError" });
  assert.equal(await replacement, "published");
  assert.deepEqual([old.disposals, published[0]?.[0]], [1, "next"]);
  let opened = false;
  await assert.rejects(
    requestSource(handoff, "blocked", () => (opened = true)),
    (error) => error === failure,
  );
  await assert.rejects(handoff.dispose(), (error) => error === failure);
  assert.deepEqual([opened, active.disposals], [false, 1]);
});

void test("dispose waits for and releases a source delivered by a late open", async () => {
  const old = source("old"),
    { handoff } = await setup(old),
    late = deferred<MarkdownOwnedGeneration>(),
    replacement = source("late");
  const opening = handoff.request({ open: () => late.promise });
  let disposed = false;
  const disposal = handoff.dispose().then(() => (disposed = true));
  assert.equal(await opening, "cancelled");
  await Promise.resolve();
  assert.deepEqual([disposed, old.disposals, replacement.disposals], [false, 0, 0]);
  late.resolve(replacement);
  await disposal;
  assert.deepEqual([disposed, old.disposals, replacement.disposals], [true, 1, 1]);
});

void test("dispose aborts and drains an owned read, releasing generations once", async () => {
  const old = source("old"),
    { handoff, published } = await setup(old),
    candidate = source("candidate"),
    gate = gateRead(candidate);
  const pending = handoff.request({ open: async () => gate.source });
  await gate.started;
  const disposal = handoff.dispose();
  assert.equal(handoff.dispose(), disposal);
  assert.equal(await pending, "cancelled");
  assert.deepEqual([old.disposals, candidate.disposals, published.length], [0, 0, 0]);
  gate.release();
  await gate.drained;
  await disposal;
  assert.deepEqual([old.disposals, candidate.disposals, published.length], [1, 1, 0]);
});
