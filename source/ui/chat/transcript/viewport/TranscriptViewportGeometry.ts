import type { TranscriptEntryRef } from "../../../sessions/transcript/Messages.ts";
import type {
  TranscriptMeasurement,
  TranscriptSourceCursor,
  TranscriptViewportAnchor,
  TranscriptViewportGeometry,
  WeightedEntryPosition,
  WeightedPositionLookup,
} from "./TranscriptViewport.ts";

interface GeometryPoint {
  readonly rank: number;
  readonly height: number;
  readonly position: WeightedEntryPosition | null;
}

export function locateAtFraction(
  geometry: TranscriptViewportGeometry,
  fraction: number,
  totalHeight: number,
  correctionBeforeRank: (rank: number) => number,
): WeightedPositionLookup {
  const clampedFraction = Math.min(1, Math.max(0, fraction));
  const position = clampedFraction * totalHeight;
  const points = correctedGeometryPoints(geometry, correctionBeforeRank);

  if (geometry.entryCount === 0)
    return {
      estimatedRank: 0,
      rankHint: 0,
      around: null,
      heightAfterAround: position,
      estimatedHeight: totalHeight,
    };

  let lower = points[0]!;
  let upper = points[points.length - 1]!;
  for (let index = 1; index < points.length; index++) {
    const point = points[index]!;
    if (point.height < position) {
      lower = point;
      continue;
    }
    upper = point;
    break;
  }

  const heightSpan = upper.height - lower.height;
  const rankSpan = upper.rank - lower.rank;
  const ratio = heightSpan <= 0 ? 0 : (position - lower.height) / heightSpan;
  const estimatedRank = Math.min(geometry.entryCount, Math.max(0, lower.rank + ratio * rankSpan));
  const rankHint = Math.min(geometry.entryCount - 1, Math.floor(estimatedRank));
  const around = lower.position ?? upper.position;
  return {
    estimatedRank,
    rankHint,
    around: around === null ? null : copyEntry(around.entry),
    heightAfterAround:
      around === null
        ? position
        : position - (basePrefixHeight(geometry, around.rank) + correctionBeforeRank(around.rank)),
    estimatedHeight: totalHeight,
  };
}

function correctedGeometryPoints(
  geometry: TranscriptViewportGeometry,
  correctionBeforeRank: (rank: number) => number,
): GeometryPoint[] {
  const points = baseGeometryPoints(geometry);
  let previousHeight = 0;
  return points.map((point) => {
    const height = Math.max(previousHeight, point.height + correctionBeforeRank(point.rank));
    previousHeight = height;
    return { ...point, height };
  });
}

export function basePrefixHeight(geometry: TranscriptViewportGeometry, rank: number): number {
  const points = baseGeometryPoints(geometry);
  for (let index = 1; index < points.length; index++) {
    const upper = points[index]!;
    if (upper.rank < rank) continue;
    const lower = points[index - 1]!;
    const span = upper.rank - lower.rank;
    if (span <= 0) return upper.height;
    const ratio = (rank - lower.rank) / span;
    return lower.height + ratio * (upper.height - lower.height);
  }
  return geometry.estimatedTotalHeight;
}

function baseGeometryPoints(geometry: TranscriptViewportGeometry): GeometryPoint[] {
  const { entryCount, estimatedTotalHeight, weightedPositions } = geometry;
  if (entryCount === 0) return [{ rank: 0, height: 0, position: null }];

  const points: GeometryPoint[] = [
    { rank: 0, height: 0, position: null },
    ...weightedPositions.map((position) => ({
      rank: position.rank,
      height: position.estimatedTop,
      position,
    })),
    { rank: entryCount, height: estimatedTotalHeight, position: null },
  ];
  points.sort((left, right) => left.rank - right.rank);
  return points;
}

export function copyAndValidateGeometry(
  geometry: TranscriptViewportGeometry,
): TranscriptViewportGeometry {
  if (
    !Number.isSafeInteger(geometry.historyRevision) ||
    geometry.historyRevision < 0 ||
    !Number.isSafeInteger(geometry.positionVersion) ||
    geometry.positionVersion < 0 ||
    !Number.isSafeInteger(geometry.entryCount) ||
    geometry.entryCount < 0 ||
    !Number.isFinite(geometry.estimatedTotalHeight) ||
    geometry.estimatedTotalHeight < 0 ||
    (geometry.entryCount === 0 && geometry.estimatedTotalHeight !== 0)
  )
    throw new TypeError("Invalid transcript geometry.");

  let previousRank = -1;
  let previousTop = -1;
  const weightedPositions = geometry.weightedPositions.map((position) => {
    if (
      !Number.isSafeInteger(position.rank) ||
      position.rank < 0 ||
      position.rank >= geometry.entryCount ||
      position.rank <= previousRank ||
      !Number.isFinite(position.estimatedTop) ||
      position.estimatedTop < previousTop ||
      position.estimatedTop > geometry.estimatedTotalHeight ||
      (position.rank === 0 && position.estimatedTop !== 0) ||
      position.entry.historyRevision !== geometry.historyRevision
    )
      throw new TypeError("Invalid weighted transcript position.");
    previousRank = position.rank;
    previousTop = position.estimatedTop;
    return { ...position, entry: copyEntry(position.entry) };
  });

  return { ...geometry, weightedPositions };
}

export function validAnchor(
  anchor: TranscriptViewportAnchor | null,
  geometry: TranscriptViewportGeometry,
): TranscriptViewportAnchor | null {
  if (anchor === null) return null;
  if (!isValidAnchor(anchor, geometry))
    throw new TypeError("Fallback anchor does not belong to the new history revision.");
  return copyAnchor(anchor);
}

export function isValidAnchor(
  anchor: TranscriptViewportAnchor,
  geometry: TranscriptViewportGeometry,
): boolean {
  const weightedPosition = geometry.weightedPositions.find(
    (position) => position.rank === anchor.rank,
  );
  return (
    anchor.entry.historyRevision === geometry.historyRevision &&
    Number.isSafeInteger(anchor.rank) &&
    anchor.rank >= 0 &&
    anchor.rank < geometry.entryCount &&
    (weightedPosition === undefined || sameEntry(weightedPosition.entry, anchor.entry)) &&
    Number.isFinite(anchor.viewportOffset) &&
    (anchor.sourceCursor === null || isValidCursor(anchor.sourceCursor))
  );
}

export function validateMeasurement(measurement: TranscriptMeasurement): void {
  validateRank(measurement.rank, Number.MAX_SAFE_INTEGER, false);
  if (
    !Number.isSafeInteger(measurement.generation) ||
    measurement.generation < 0 ||
    !Number.isFinite(measurement.estimatedHeight) ||
    measurement.estimatedHeight < 0 ||
    !Number.isFinite(measurement.measuredHeight) ||
    measurement.measuredHeight < 0 ||
    (measurement.afterSourceCursor !== null && !isValidCursor(measurement.afterSourceCursor))
  )
    throw new TypeError("Invalid transcript measurement.");
}

export function validateRank(rank: number, maximum: number, allowEnd: boolean): void {
  if (!Number.isSafeInteger(rank) || rank < 0 || (allowEnd ? rank > maximum : rank >= maximum))
    throw new TypeError("Transcript rank is out of range.");
}

function isValidCursor(cursor: TranscriptSourceCursor): boolean {
  return (
    Number.isSafeInteger(cursor.segmentIndex) &&
    cursor.segmentIndex >= 0 &&
    Number.isSafeInteger(cursor.utf8Offset) &&
    cursor.utf8Offset >= 0
  );
}

export function compareCursor(left: TranscriptSourceCursor, right: TranscriptSourceCursor): number {
  return left.segmentIndex - right.segmentIndex || left.utf8Offset - right.utf8Offset;
}

export function measurementKey(rank: number, cursor: TranscriptSourceCursor | null): string {
  return cursor === null ? `${rank}:end` : `${rank}:${cursor.segmentIndex}:${cursor.utf8Offset}`;
}

export function copyCursor(cursor: TranscriptSourceCursor | null): TranscriptSourceCursor | null {
  return cursor === null ? null : { ...cursor };
}

function copyEntry(entry: TranscriptEntryRef): TranscriptEntryRef {
  return { ...entry };
}

export function copyAnchor(
  anchor: TranscriptViewportAnchor | null,
): TranscriptViewportAnchor | null {
  return anchor === null
    ? null
    : { ...anchor, entry: copyEntry(anchor.entry), sourceCursor: copyCursor(anchor.sourceCursor) };
}

export function sameEntry(left: TranscriptEntryRef, right: TranscriptEntryRef): boolean {
  return (
    left.executionId === right.executionId &&
    left.entryId === right.entryId &&
    left.kind === right.kind &&
    left.historyRevision === right.historyRevision
  );
}
