import type { TranscriptPositionWindow, TranscriptPositionedEntry } from "./Messages.ts";
import type { TranscriptViewportGeometry, WeightedEntryPosition } from "./TranscriptViewport.ts";

// Browser-owned estimate scale; backend weights are deliberately not CSS pixels.
export interface TranscriptLayoutProfile {
  readonly version: 1;
  readonly pixelsPerWeight: number;
}

export function transcriptViewportGeometry(
  sessionId: string,
  window: TranscriptPositionWindow,
  profile: TranscriptLayoutProfile,
): TranscriptViewportGeometry {
  validateProfile(profile);
  const { state } = window;
  if (
    sessionId.length === 0 ||
    state.sessionId !== sessionId ||
    !Number.isSafeInteger(state.historyRevision) ||
    state.historyRevision < 0 ||
    !Number.isSafeInteger(state.positionVersion) ||
    state.positionVersion < 0 ||
    !Number.isSafeInteger(state.entryCount) ||
    state.entryCount < 0 ||
    !Number.isFinite(state.totalWeight) ||
    state.totalWeight < 0
  )
    throw new TypeError("Invalid transcript position summary.");

  const totalHeight = toPixels(state.totalWeight, profile.pixelsPerWeight);
  if ((state.entryCount === 0) !== (state.totalWeight === 0))
    throw new TypeError("Empty transcript positions must have zero total weight.");

  const positioned: {
    descriptor: TranscriptPositionedEntry["descriptor"] | null;
    position: TranscriptPositionedEntry["position"];
  }[] = window.entries.map((item) => ({ descriptor: item.descriptor, position: item.position }));
  for (let index = 1; index < positioned.length; index++)
    if (positioned[index]!.position.rank <= positioned[index - 1]!.position.rank)
      throw new TypeError("Transcript positions must be ordered by rank.");
  if (
    window.target !== null &&
    !positioned.some(({ position }) => samePosition(position, window.target!))
  )
    positioned.push({ descriptor: null, position: window.target });
  positioned.sort((left, right) => left.position.rank - right.position.rank);

  const identities = new Set<string>();
  let previousRank = -1;
  let previousPrefix = -1;
  let previousWeight = 0;
  const weightedPositions: WeightedEntryPosition[] = positioned.map(({ descriptor, position }) => {
    const entry = position;
    const key = `${entry.executionId}\u0000${entry.entryId}`;
    if (
      entry.historyRevision !== state.historyRevision ||
      !Number.isSafeInteger(entry.rank) ||
      entry.rank < 0 ||
      entry.rank >= state.entryCount ||
      !Number.isFinite(entry.weight) ||
      entry.weight < 0 ||
      !Number.isFinite(entry.prefixWeight) ||
      entry.prefixWeight < 0 ||
      entry.prefixWeight + entry.weight > state.totalWeight ||
      (entry.rank === 0 && entry.prefixWeight !== 0) ||
      entry.rank <= previousRank ||
      entry.prefixWeight < previousPrefix + previousWeight ||
      identities.has(key) ||
      (descriptor !== null && !sameEntry(descriptor.entry, entry))
    )
      throw new TypeError("Invalid transcript position window.");
    identities.add(key);
    previousRank = entry.rank;
    previousPrefix = entry.prefixWeight;
    previousWeight = entry.weight;
    return {
      entry: {
        executionId: entry.executionId,
        entryId: entry.entryId,
        kind: entry.kind,
        historyRevision: entry.historyRevision,
      },
      rank: entry.rank,
      estimatedTop: toPixels(entry.prefixWeight, profile.pixelsPerWeight),
    };
  });

  return {
    historyRevision: state.historyRevision,
    positionVersion: state.positionVersion,
    entryCount: state.entryCount,
    estimatedTotalHeight: totalHeight,
    weightedPositions,
  };
}

function validateProfile(profile: TranscriptLayoutProfile): void {
  if (
    profile.version !== 1 ||
    !Number.isFinite(profile.pixelsPerWeight) ||
    profile.pixelsPerWeight <= 0
  )
    throw new TypeError("Invalid transcript layout profile.");
}

function toPixels(weight: number, pixelsPerWeight: number): number {
  const pixels = weight * pixelsPerWeight;
  if (!Number.isFinite(pixels))
    throw new TypeError("Transcript geometry exceeds safe pixel bounds.");
  return pixels;
}

function sameEntry(
  left: {
    readonly executionId: string;
    readonly entryId: string;
    readonly kind: string;
    readonly historyRevision: number;
  },
  right: {
    readonly executionId: string;
    readonly entryId: string;
    readonly kind: string;
    readonly historyRevision: number;
  },
): boolean {
  return (
    left.executionId === right.executionId &&
    left.entryId === right.entryId &&
    left.kind === right.kind &&
    left.historyRevision === right.historyRevision
  );
}

function samePosition(
  left: TranscriptPositionedEntry["position"],
  right: TranscriptPositionedEntry["position"],
): boolean {
  return (
    left.rank === right.rank &&
    left.executionId === right.executionId &&
    left.entryId === right.entryId
  );
}
