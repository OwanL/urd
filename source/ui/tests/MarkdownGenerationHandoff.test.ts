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
const sourceWithChild = (generation: string) => {
  const pieces = Array.from({ length: 8 }, (_, id) => createMarkdownPiece(id, 2, `p${id}`));
  return new SyntheticMarkdownPageSource(
    pieces,
    [
      { id: 0, kind: "document", children: [1, 1], pieces: [0, pieces.length] },
      { id: 1, kind: "code", parent: 0, children: [2, 1], pieces: [0, pieces.length] },
      { id: 2, kind: "codeLine", parent: 1, pieces: [0, pieces.length] },
    ],
    { generation, piecePageSize: 1 },
  );
};
const gateChildLocate = (backing: SyntheticMarkdownPageSource) => {
  const started = deferred<void>(),
    gate = deferred<void>(),
    aborted = deferred<void>();
  const locate = backing.locateChild.bind(backing);
  backing.locateChild = async (contextId, childIndex, readSignal) => {
    readSignal.addEventListener("abort", () => aborted.resolve(), { once: true });
    started.resolve();
    await gate.promise;
    return locate(contextId, childIndex, signal());
  };
  return {
    started: started.promise,
    aborted: aborted.promise,
    release: () => gate.resolve(),
  };
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

void test("stages a distant same-lease seek and keeps disjoint selected pieces", async () => {
  const old = source(
    "old",
    Array.from({ length: 100 }, (_, index) => index),
    1,
  );
  const { handoff, published } = await setup(old);
  const prior = handoff.snapshot;
  const restore = anchor("old", "90", "90", 14);
  const snapshot = await handoff.seek(
    { generation: "old", index: 90, offset: 14 },
    2,
    [{ from: 0, next: 1 }],
    async (window, _cursor, readSignal) => {
      const active = { from: 89, next: 91 };
      while (window.snapshot.next < active.next) await window.append(readSignal);
      const snapshot = await window.trim(
        { generation: "old", active, retain: [{ from: 0, next: 1 }] },
        readSignal,
      );
      return { snapshot, anchor: restore };
    },
    () => true,
    signal(),
  );

  assert.notEqual(snapshot, prior);
  assert.equal(handoff.snapshot, snapshot);
  assert.deepEqual([...snapshot.pieces.keys()], [0, 89, 90]);
  assert.deepEqual(snapshot.displayRanges, [
    { from: 0, next: 1 },
    { from: 89, next: 91 },
  ]);
  assert.deepEqual(
    old.pieceRequests.map(({ index }) => index),
    [0, 89, 90],
  );
  assert.deepEqual(published, [["old", restore]]);
  assert.equal(old.disposals, 0, "A same-generation seek retains its active lease");
  await handoff.dispose();
  assert.equal(old.disposals, 1);
});

void test("aborted staged seek leaves the old snapshot and drains before disposal", async () => {
  const old = source(
    "old",
    Array.from({ length: 12 }, (_, index) => index),
    1,
  );
  const { handoff, published } = await setup(old);
  const prior = handoff.snapshot;
  const gate = gateRead(old);
  const abort = new AbortController();
  const seek = handoff.seek(
    { generation: "old", index: 8, offset: 0 },
    2,
    [],
    async (window, _cursor, readSignal) => {
      await window.append(readSignal);
      throw new Error("aborted seek must not prepare");
    },
    () => true,
    abort.signal,
  );
  await gate.started;
  abort.abort();
  const disposal = handoff.dispose();
  await gate.aborted;
  await Promise.resolve();
  assert.equal(old.disposals, 0);
  assert.equal(handoff.snapshot, prior);
  gate.release();
  await assert.rejects(seek, { name: "AbortError" });
  await disposal;
  assert.equal(old.disposals, 1);
  assert.deepEqual(published, []);
});

void test("child lookup is generation-fenced and drained before lease disposal", async () => {
  const old = sourceWithChild("old");
  const { handoff, published } = await setup(old);
  const prior = handoff.snapshot;
  const gate = gateChildLocate(old);
  let prepared = false;
  const seek = handoff.seek(
    { generation: "old", contextId: 1, childIndex: 0, offset: 20 },
    2,
    [],
    async (window) => {
      prepared = true;
      return { snapshot: window.snapshot, anchor: null };
    },
    () => true,
    signal(),
  );
  await gate.started;
  let disposed = false;
  const disposal = handoff.dispose().then(() => (disposed = true));
  await gate.aborted;
  await Promise.resolve();
  assert.equal(disposed, false);
  assert.equal(handoff.snapshot, prior);
  gate.release();
  await assert.rejects(seek, { name: "AbortError" });
  await disposal;
  assert.equal(prepared, false);
  assert.equal(old.disposals, 1);
  assert.deepEqual(old.childLocateRequests, [{ contextId: 1, childIndex: 0 }]);
  assert.deepEqual(published, []);
});

void test("replacement drains an in-flight child lookup before switching leases", async () => {
  const old = sourceWithChild("old");
  const { handoff, published } = await setup(old);
  const gate = gateChildLocate(old);
  const seek = handoff.seek(
    { generation: "old", contextId: 1, childIndex: 0, offset: 0 },
    2,
    [],
    async (window, _cursor, readSignal) => {
      const snapshot = await window.append(readSignal);
      return { snapshot, anchor: null };
    },
    () => true,
    signal(),
  );
  await gate.started;
  const replacementSource = source("replacement");
  const replacement = handoff.request({ open: async () => replacementSource });
  await gate.aborted;
  assert.equal(old.disposals, 0);
  gate.release();
  await assert.rejects(seek, { name: "AbortError" });
  assert.equal(await replacement, "published");
  assert.deepEqual([handoff.snapshot.generation, old.disposals], ["replacement", 1]);
  assert.deepEqual(
    published.map(([generation]) => generation),
    ["replacement"],
  );
  await handoff.dispose();
  assert.equal(replacementSource.disposals, 1);
});

void test("failed seek preparation never commits the staged window", async () => {
  const old = source("old", [0, 1, 2, 3], 1);
  const { handoff, published } = await setup(old);
  const prior = handoff.snapshot;
  const failure = new Error("seek preparation failed");
  await assert.rejects(
    handoff.seek(
      { generation: "old", index: 2, offset: 0 },
      2,
      [],
      async () => {
        throw failure;
      },
      () => true,
      signal(),
    ),
    (error) => error === failure,
  );
  assert.equal(handoff.snapshot, prior);
  assert.deepEqual(published, []);
  await handoff.dispose();
});

void test("seek does not publish if retained focus or selection changes while staging", async () => {
  const old = source("old", [0, 1, 2, 3], 1);
  const { handoff, published } = await setup(old);
  const prior = handoff.snapshot;
  await assert.rejects(
    handoff.seek(
      { generation: "old", index: 2, offset: 0 },
      2,
      [],
      async (window) => ({ snapshot: window.snapshot, anchor: null }),
      () => false,
      signal(),
    ),
    { name: "AbortError" },
  );
  assert.equal(handoff.snapshot, prior);
  assert.deepEqual(published, []);
  await handoff.dispose();
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

void test("publishes a same-generation reconnect and releases the old lease once", async () => {
  const old = source("same"),
    { handoff, published } = await setup(old),
    reconnect = source("same");

  assert.equal(await handoff.request({ open: async () => reconnect }), "published");
  assert.deepEqual(
    [handoff.snapshot.generation, reconnect.disposals, old.disposals],
    ["same", 0, 1],
  );
  assert.equal(published.length, 1);
  await handoff.dispose();
  assert.deepEqual([old.disposals, reconnect.disposals], [1, 1]);
});

void test("rejects a contradictory same-generation manifest without replacing the active lease", async () => {
  const old = source("same"),
    { handoff, published } = await setup(old),
    contradictory = source("same", [0], Infinity, { sourceUtf8Bytes: 2 });

  await assert.rejects(handoff.request({ open: async () => contradictory }), /manifest differs/);
  assert.deepEqual(
    [handoff.snapshot.generation, published.length, old.disposals, contradictory.disposals],
    ["same", 0, 0, 1],
  );
  await handoff.dispose();
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
