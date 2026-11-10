import assert from "node:assert/strict";
import test from "node:test";
import type { TranscriptEntryRef } from "./Messages.ts";
import {
  TranscriptViewport,
  type TranscriptMeasurement,
  type TranscriptSourceCursor,
  type TranscriptViewportAnchor,
  type TranscriptViewportGeometry,
} from "./TranscriptViewport.ts";

function entry(rank: number, historyRevision = 1): TranscriptEntryRef {
  return {
    executionId: `execution-${rank}`,
    entryId: `entry-${rank}`,
    kind: "text",
    historyRevision,
  };
}

function geometry(
  entryCount: number,
  historyRevision = 1,
  estimatedTotalHeight = entryCount * 100,
  weightedRanks: readonly number[] = [],
  positionVersion = 0,
): TranscriptViewportGeometry {
  return {
    historyRevision,
    positionVersion,
    entryCount,
    estimatedTotalHeight,
    weightedPositions: weightedRanks.map((rank) => ({
      entry: entry(rank, historyRevision),
      rank,
      estimatedTop: entryCount === 100_000 ? rank * 10_000_000 : rank * 100,
    })),
  };
}

function anchor(
  rank: number,
  historyRevision = 1,
  sourceCursor: TranscriptSourceCursor | null = null,
): TranscriptViewportAnchor {
  return { entry: entry(rank, historyRevision), rank, sourceCursor, viewportOffset: -12 };
}

function measurement(
  rank: number,
  generation: number,
  estimatedHeight: number,
  measuredHeight: number,
  afterSourceCursor: TranscriptSourceCursor | null = null,
  historyRevision = 1,
): TranscriptMeasurement {
  return {
    entry: entry(rank, historyRevision),
    rank,
    afterSourceCursor,
    estimatedHeight,
    measuredHeight,
    generation,
  };
}

void test("locates distant positions from sparse weighted inputs at 100,000-entry scale", () => {
  const viewport = new TranscriptViewport(
    geometry(100_000, 1, 1_000_000_000_000, [0, 50_000, 99_999]),
  );

  const location = viewport.locateAtFraction(0.9);

  assert.equal(viewport.snapshot().entryCount, 100_000);
  assert.equal(location.around?.entryId, "entry-50000");
  assert.ok(location.rankHint >= 89_998 && location.rankHint <= 90_001);
  assert.ok(location.heightAfterAround > 0);
  assert.equal(viewport.jumpToAnchor(anchor(99_999)), true);
  assert.equal(viewport.snapshot().anchor?.entry.entryId, "entry-99999");
});

void test("height corrections preserve the visible source anchor and ignore later content", () => {
  const visible = anchor(5, 1, { segmentIndex: 0, utf8Offset: 20 });
  const viewport = new TranscriptViewport(geometry(10, 1, 1_000, [0, 5, 9]));
  assert.equal(viewport.recordUserScroll(visible, false), true);

  const earlier = viewport.recordMeasurement(measurement(2, 0, 100, 150));
  const earlierWithinEntry = viewport.recordMeasurement(
    measurement(5, 0, 20, 32, { segmentIndex: 0, utf8Offset: 10 }),
  );
  const later = viewport.recordMeasurement(measurement(8, 0, 100, 120));

  assert.equal(earlier.scrollAdjustment, 50);
  assert.equal(earlierWithinEntry.scrollAdjustment, 12);
  assert.equal(later.scrollAdjustment, 0);
  assert.equal(viewport.estimatedPrefixHeight(5), 550);
  assert.deepEqual(viewport.snapshot().anchor, visible);
  assert.equal(viewport.snapshot().followsLatest, false);
});

void test("revised estimates compensate geometry changes above the source anchor", () => {
  const original = geometry(10, 1, 1_000, [0, 5, 9]);
  const viewport = new TranscriptViewport(original);
  const visible = anchor(5);
  assert.equal(viewport.recordUserScroll(visible, false), true);
  const revised = geometry(10, 1, 1_200, [0, 5, 9], 1);
  const changed = {
    ...revised,
    weightedPositions: revised.weightedPositions.map((position) =>
      position.rank === 5 ? { ...position, estimatedTop: 700 } : position,
    ),
  };

  const update = viewport.updateGeometry(changed);

  assert.equal(update.scrollAdjustment, 200);
  assert.equal(viewport.estimatedPrefixHeight(5), 700);
  assert.deepEqual(update.anchor, visible);
});

void test("appends move followers to the new tail without moving readers", () => {
  const original = geometry(2, 1, 200, [0, 1]);
  const appended = geometry(3, 1, 300, [0, 1, 2], 1);
  const follower = new TranscriptViewport(original);
  const reader = new TranscriptViewport(original);
  const readingAnchor = anchor(0);
  assert.equal(reader.recordUserScroll(readingAnchor, false), true);

  const followingUpdate = follower.updateGeometry(appended);
  const readingUpdate = reader.updateGeometry(appended);

  assert.equal(followingUpdate.followsLatest, true);
  assert.equal(followingUpdate.latestPosition, 300);
  assert.equal(followingUpdate.anchor, null);
  assert.equal(readingUpdate.followsLatest, false);
  assert.deepEqual(readingUpdate.anchor, readingAnchor);
  assert.equal(readingUpdate.latestPosition, null);
});

void test("same-revision geometry rejects stale append and estimate versions", () => {
  const viewport = new TranscriptViewport(geometry(2, 1, 200, [0, 1], 4));
  const readingAnchor = anchor(0);
  assert.equal(viewport.recordUserScroll(readingAnchor, false), true);

  const appended = viewport.updateGeometry(geometry(3, 1, 300, [0, 1, 2], 6));
  assert.equal(appended.anchor?.entry.entryId, readingAnchor.entry.entryId);
  assert.equal(appended.followsLatest, false);

  const beforeStaleEstimate = viewport.snapshot();
  const staleEstimate = viewport.updateGeometry(geometry(3, 1, 900, [0, 1, 2], 5));
  assert.deepEqual(viewport.snapshot(), beforeStaleEstimate);
  assert.deepEqual(staleEstimate.anchor, readingAnchor);
  assert.equal(staleEstimate.scrollAdjustment, 0);

  const acceptedEstimate = viewport.updateGeometry(geometry(3, 1, 360, [0, 1, 2], 7));
  assert.equal(viewport.snapshot().estimatedTotalHeight, 360);
  assert.equal(acceptedEstimate.followsLatest, false);
  assert.deepEqual(acceptedEstimate.anchor, readingAnchor);

  const duplicateVersion = viewport.updateGeometry(geometry(3, 1, 500, [0, 1, 2], 7));
  assert.equal(viewport.snapshot().estimatedTotalHeight, 360);
  assert.equal(duplicateVersion.scrollAdjustment, 0);
});

void test("width changes invalidate measurements while preserving anchor and follow intent", () => {
  const viewport = new TranscriptViewport(geometry(4, 1, 400, [0, 2]));
  const visible = anchor(2);
  assert.equal(viewport.recordUserScroll(visible, false), true);
  const firstWidth = viewport.setWidth(800);
  const correction = viewport.recordMeasurement(measurement(0, firstWidth.generation, 100, 150));
  assert.equal(correction.scrollAdjustment, 50);

  const resized = viewport.setWidth(600);

  assert.equal(resized.scrollAdjustment, -50);
  assert.equal(resized.generation, firstWidth.generation + 1);
  assert.equal(viewport.snapshot().measurementCount, 0);
  assert.deepEqual(resized.anchor, visible);
  assert.equal(
    viewport.recordMeasurement(measurement(1, firstWidth.generation, 100, 130)).accepted,
    false,
  );
});

void test("measurement eviction removes corrections and makes in-flight reports stale", () => {
  const viewport = new TranscriptViewport(geometry(10, 1, 1_000, [0, 5, 9]));
  assert.equal(viewport.recordUserScroll(anchor(5), false), true);
  const generation = viewport.snapshot().generation;
  viewport.recordMeasurement(measurement(2, generation, 100, 140));
  viewport.recordMeasurement(measurement(8, generation, 100, 150));

  const evicted = viewport.evictMeasurements(0, 5);

  assert.equal(evicted.scrollAdjustment, -40);
  assert.equal(evicted.generation, generation + 1);
  assert.equal(viewport.snapshot().measurementCount, 1);
  assert.equal(viewport.estimatedPrefixHeight(5), 500);
  assert.equal(viewport.recordMeasurement(measurement(2, generation, 100, 160)).accepted, false);
});

void test("revision changes reject stale anchors and measurements, using the explicit fallback", () => {
  const viewport = new TranscriptViewport(geometry(10, 1, 1_000, [0, 4, 9]));
  const oldAnchor = anchor(7);
  assert.equal(viewport.recordUserScroll(oldAnchor, false), true);
  const staleMeasurement = measurement(2, viewport.snapshot().generation, 100, 140);
  viewport.recordMeasurement(staleMeasurement);
  const fallback = anchor(4, 2, { segmentIndex: 0, utf8Offset: 64 });

  const update = viewport.updateGeometry(geometry(8, 2, 800, [0, 4, 7]), fallback);

  assert.equal(update.followsLatest, false);
  assert.deepEqual(update.anchor, fallback);
  assert.equal(viewport.snapshot().measurementCount, 0);
  assert.equal(viewport.recordAnchor(oldAnchor), false);
  assert.equal(viewport.jumpToAnchor(oldAnchor), false);
  assert.equal(viewport.recordMeasurement(staleMeasurement).accepted, false);
});

void test("out-of-order geometry cannot replace a newer revision or reset viewport state", () => {
  const viewport = new TranscriptViewport(geometry(10, 1, 1_000, [0, 5, 9]));
  const previousAnchor = anchor(5, 1);
  assert.equal(viewport.recordUserScroll(previousAnchor, false), true);
  const visible = anchor(5, 2);
  const revisionTwo = geometry(10, 2, 1_100, [0, 5, 9]);
  const advanced = viewport.updateGeometry(revisionTwo, visible);
  viewport.recordMeasurement(measurement(2, advanced.generation, 100, 140, null, 2));
  const beforeStaleUpdate = viewport.snapshot();

  const staleUpdate = viewport.updateGeometry(geometry(3, 1, 300, [0, 2]));

  assert.deepEqual(viewport.snapshot(), beforeStaleUpdate);
  assert.deepEqual(staleUpdate.anchor, visible);
  assert.equal(staleUpdate.followsLatest, false);
  assert.equal(staleUpdate.scrollAdjustment, 0);

  const follower = new TranscriptViewport(geometry(2, 1));
  follower.updateGeometry(geometry(3, 2), anchor(1, 2));
  const followingState = follower.snapshot();
  follower.updateGeometry(geometry(1, 1));
  assert.deepEqual(follower.snapshot(), followingState);
});

void test("only genuine user navigation changes follow intent; Jump to latest restores it", () => {
  const viewport = new TranscriptViewport(geometry(5));
  const readingAnchor = anchor(2);

  assert.equal(viewport.snapshot().followsLatest, true);
  assert.equal(viewport.recordAnchor(readingAnchor), true);
  assert.equal(viewport.snapshot().followsLatest, true);
  assert.equal(viewport.recordUserScroll(readingAnchor, false), true);
  assert.equal(viewport.snapshot().followsLatest, false);
  assert.equal(viewport.recordAnchor(anchor(3)), true);
  assert.equal(viewport.snapshot().followsLatest, false);

  const jump = viewport.jumpToLatest();

  assert.equal(jump.followsLatest, true);
  assert.equal(jump.latestPosition, 500);
  assert.equal(jump.anchor, null);
  assert.equal(viewport.jumpToAnchor(anchor(0)), true);
  assert.equal(viewport.snapshot().followsLatest, false);
});
