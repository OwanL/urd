import assert from "node:assert/strict";
import test from "node:test";
import type {
  TranscriptEntryDescriptor,
  TranscriptPosition,
  TranscriptPositionWindow,
} from "./Messages.ts";
import { transcriptViewportGeometry, type TranscriptLayoutProfile } from "./TranscriptGeometry.ts";

const profile: TranscriptLayoutProfile = { version: 1, pixelsPerWeight: 8 };

function positioned(rank: number, prefixWeight: number, weight = 2, revision = 3) {
  const position: TranscriptPosition = {
    executionId: `exec-${rank}`,
    entryId: `entry-${rank}`,
    kind: "text",
    historyRevision: revision,
    rank,
    weight,
    prefixWeight,
  };
  const descriptor: TranscriptEntryDescriptor = {
    entry: {
      executionId: position.executionId,
      entryId: position.entryId,
      kind: position.kind,
      historyRevision: revision,
    },
    createdAt: null,
    text: null,
    tool: null,
  };
  return { descriptor, position };
}

function windowOf(
  entryCount: number,
  totalWeight: number,
  entries = [positioned(0, 0)],
  revision = 3,
): TranscriptPositionWindow {
  return {
    state: {
      sessionId: "session-a",
      historyRevision: revision,
      positionVersion: 7,
      entryCount,
      totalWeight,
    },
    target: null,
    entries,
    toolLabels: {},
    hasBefore: false,
    hasAfter: false,
    settlements: [],
  };
}

void test("converts abstract prefixes and totals to browser-owned CSS pixel estimates", () => {
  const result = transcriptViewportGeometry("session-a", windowOf(4, 20), profile);
  assert.equal(result.estimatedTotalHeight, 160);
  assert.equal(result.weightedPositions[0]?.estimatedTop, 0);
  assert.equal(result.weightedPositions[0]?.rank, 0);
  assert.equal(result.positionVersion, 7);
});

void test("preserves bounded middle windows and includes an independently located target sparsely", () => {
  const middle = positioned(50_000, 100_000);
  const target = positioned(90_000, 180_000).position;
  const result = transcriptViewportGeometry(
    "session-a",
    {
      ...windowOf(100_000, 200_000, [middle]),
      target,
    },
    profile,
  );
  assert.equal(result.entryCount, 100_000);
  assert.deepEqual(
    result.weightedPositions.map(({ rank, estimatedTop }) => [rank, estimatedTop]),
    [
      [50_000, 800_000],
      [90_000, 1_440_000],
    ],
  );
});

void test("supports empty and large safe-number totals without expanding entries", () => {
  const empty = transcriptViewportGeometry("session-a", windowOf(0, 0, []), profile);
  assert.equal(empty.estimatedTotalHeight, 0);
  assert.deepEqual(empty.weightedPositions, []);

  const totalWeight = Number.MAX_SAFE_INTEGER / 4;
  const large = transcriptViewportGeometry(
    "session-a",
    windowOf(100_000, totalWeight, []),
    profile,
  );
  assert.equal(large.weightedPositions.length, 0);
  assert.ok(Number.isFinite(large.estimatedTotalHeight));
});

void test("rejects inconsistent session, revision, rank, identity and abstract weights", () => {
  assert.throws(() => transcriptViewportGeometry("other", windowOf(1, 2), profile), TypeError);
  assert.throws(
    () =>
      transcriptViewportGeometry("session-a", windowOf(1, 2, [positioned(0, 0, 2, 4)]), profile),
    TypeError,
  );
  assert.throws(
    () =>
      transcriptViewportGeometry(
        "session-a",
        windowOf(2, 4, [positioned(1, 0), positioned(0, 2)]),
        profile,
      ),
    TypeError,
  );
  assert.throws(
    () =>
      transcriptViewportGeometry(
        "session-a",
        windowOf(3, 4, [positioned(0, 0), positioned(2, 1)]),
        profile,
      ),
    TypeError,
  );
  assert.throws(
    () => transcriptViewportGeometry("session-a", windowOf(1, 2, [positioned(0, 1, 2)]), profile),
    TypeError,
  );
  assert.throws(
    () => transcriptViewportGeometry("session-a", windowOf(0, 1, []), profile),
    TypeError,
  );
  const mismatch = positioned(0, 0);
  assert.throws(
    () =>
      transcriptViewportGeometry(
        "session-a",
        windowOf(1, 2, [
          {
            ...mismatch,
            descriptor: {
              ...mismatch.descriptor,
              entry: { ...mismatch.descriptor.entry, entryId: "other" },
            },
          },
        ]),
        profile,
      ),
    TypeError,
  );
});

void test("layout profile changes alter only estimated browser geometry", () => {
  const input = windowOf(4, 20);
  const compact = transcriptViewportGeometry("session-a", input, profile);
  const spacious = transcriptViewportGeometry("session-a", input, {
    version: 1,
    pixelsPerWeight: 12,
  });
  assert.equal(compact.estimatedTotalHeight, 160);
  assert.equal(spacious.estimatedTotalHeight, 240);
  assert.equal(input.state.totalWeight, 20);
  assert.throws(
    () => transcriptViewportGeometry("session-a", input, { version: 1, pixelsPerWeight: 0 }),
    TypeError,
  );
});
