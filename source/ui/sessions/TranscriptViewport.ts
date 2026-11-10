import type { TranscriptEntryRef } from "./Messages.ts";

export interface TranscriptSourceCursor {
  readonly segmentIndex: number;
  readonly utf8Offset: number;
}

export interface TranscriptViewportAnchor {
  readonly entry: TranscriptEntryRef;
  // Revision-local rank is geometry, not part of the stable entry identity.
  readonly rank: number;
  readonly sourceCursor: TranscriptSourceCursor | null;
  readonly viewportOffset: number;
}

// A sparse prefix-height observation supplied with the history position index.
export interface WeightedEntryPosition {
  readonly entry: TranscriptEntryRef;
  readonly rank: number;
  readonly estimatedTop: number;
}

export interface TranscriptViewportGeometry {
  readonly historyRevision: number;
  readonly entryCount: number;
  readonly estimatedTotalHeight: number;
  readonly weightedPositions: readonly WeightedEntryPosition[];
}

export interface TranscriptViewportSnapshot {
  readonly historyRevision: number;
  readonly entryCount: number;
  readonly estimatedTotalHeight: number;
  readonly correctedTotalHeight: number;
  readonly followsLatest: boolean;
  readonly anchor: TranscriptViewportAnchor | null;
  readonly width: number | null;
  readonly generation: number;
  readonly measurementCount: number;
}

export interface TranscriptViewportUpdate {
  readonly anchor: TranscriptViewportAnchor | null;
  readonly followsLatest: boolean;
  readonly scrollAdjustment: number;
  readonly latestPosition: number | null;
  readonly generation: number;
}

export interface TranscriptMeasurement {
  readonly entry: TranscriptEntryRef;
  readonly rank: number;
  // The correction applies after this source position; null means after the entry.
  readonly afterSourceCursor: TranscriptSourceCursor | null;
  readonly estimatedHeight: number;
  readonly measuredHeight: number;
  readonly generation: number;
}

export interface TranscriptMeasurementResult {
  readonly accepted: boolean;
  readonly correction: number;
  readonly scrollAdjustment: number;
  readonly generation: number;
}

export interface WeightedPositionLookup {
  readonly estimatedRank: number;
  readonly rankHint: number;
  readonly around: TranscriptEntryRef | null;
  readonly heightAfterAround: number;
  readonly estimatedHeight: number;
}

interface HeightCorrection {
  readonly rank: number;
  readonly afterSourceCursor: TranscriptSourceCursor | null;
  readonly correction: number;
}

interface GeometryPoint {
  readonly rank: number;
  readonly height: number;
  readonly position: WeightedEntryPosition | null;
}

export class TranscriptViewport {
  private geometry: TranscriptViewportGeometry;
  private followsLatest = true;
  private anchor: TranscriptViewportAnchor | null = null;
  private width: number | null = null;
  private generation = 0;
  private readonly measurements = new Map<string, HeightCorrection>();

  constructor(geometry: TranscriptViewportGeometry) {
    this.geometry = copyAndValidateGeometry(geometry);
  }

  snapshot(): TranscriptViewportSnapshot {
    return {
      historyRevision: this.geometry.historyRevision,
      entryCount: this.geometry.entryCount,
      estimatedTotalHeight: this.geometry.estimatedTotalHeight,
      correctedTotalHeight: this.correctedTotalHeight(),
      followsLatest: this.followsLatest,
      anchor: copyAnchor(this.anchor),
      width: this.width,
      generation: this.generation,
      measurementCount: this.measurements.size,
    };
  }

  // Same-revision updates describe appends or revised estimates, never removed entries.
  updateGeometry(
    geometry: TranscriptViewportGeometry,
    fallbackAnchor: TranscriptViewportAnchor | null = null,
  ): TranscriptViewportUpdate {
    const next = copyAndValidateGeometry(geometry);
    if (next.historyRevision < this.geometry.historyRevision) return this.updateResult(0);

    const revisionChanged = next.historyRevision !== this.geometry.historyRevision;
    const oldAnchorTop =
      !revisionChanged && this.anchor !== null
        ? basePrefixHeight(this.geometry, this.anchor.rank)
        : null;

    if (!revisionChanged && next.entryCount < this.geometry.entryCount)
      throw new TypeError("Transcript geometry cannot remove entries without a revision change.");
    if (fallbackAnchor !== null && !revisionChanged)
      throw new TypeError("A fallback anchor is only valid when the history revision changes.");
    const nextAnchor = revisionChanged ? validAnchor(fallbackAnchor, next) : null;

    if (revisionChanged) {
      this.measurements.clear();
      this.generation++;
      this.anchor = this.followsLatest ? null : nextAnchor;
    }

    this.geometry = next;
    const scrollAdjustment =
      oldAnchorTop === null || this.anchor === null
        ? 0
        : basePrefixHeight(next, this.anchor.rank) - oldAnchorTop;
    return this.updateResult(scrollAdjustment);
  }

  // Capturing a rendered anchor does not infer user intent from a scroll event.
  recordAnchor(anchor: TranscriptViewportAnchor | null): boolean {
    if (anchor !== null && !isValidAnchor(anchor, this.geometry)) return false;
    this.anchor = copyAnchor(anchor);
    return true;
  }

  // Call only for an actual user gesture, not layout compensation or programmatic scrolling.
  recordUserScroll(anchor: TranscriptViewportAnchor | null, atLatest: boolean): boolean {
    if (atLatest) {
      if (anchor !== null && !isValidAnchor(anchor, this.geometry)) return false;
      this.followsLatest = true;
      this.anchor = null;
      return true;
    }

    if (anchor === null || !isValidAnchor(anchor, this.geometry)) return false;
    this.followsLatest = false;
    this.anchor = copyAnchor(anchor);
    return true;
  }

  jumpToAnchor(anchor: TranscriptViewportAnchor): boolean {
    if (!isValidAnchor(anchor, this.geometry)) return false;
    this.followsLatest = false;
    this.anchor = copyAnchor(anchor);
    return true;
  }

  jumpToLatest(): TranscriptViewportUpdate {
    this.followsLatest = true;
    this.anchor = null;
    return this.updateResult(0);
  }

  setWidth(width: number): TranscriptViewportUpdate {
    if (!Number.isFinite(width) || width <= 0) throw new TypeError("Width must be positive.");
    if (width === this.width) return this.updateResult(0);

    const removedCorrection = this.correctionBeforeAnchor(this.anchor);
    this.width = width;
    this.measurements.clear();
    this.generation++;
    return this.updateResult(-removedCorrection);
  }

  // Use after source/layout changes that make otherwise same-width measurements stale.
  invalidateMeasurements(): TranscriptViewportUpdate {
    const removedCorrection = this.correctionBeforeAnchor(this.anchor);
    this.measurements.clear();
    this.generation++;
    return this.updateResult(-removedCorrection);
  }

  recordMeasurement(measurement: TranscriptMeasurement): TranscriptMeasurementResult {
    validateMeasurement(measurement);
    if (
      measurement.generation !== this.generation ||
      measurement.entry.historyRevision !== this.geometry.historyRevision ||
      measurement.rank >= this.geometry.entryCount ||
      !this.matchesKnownEntry(measurement.entry, measurement.rank)
    )
      return this.measurementResult(false, 0, 0);

    const key = measurementKey(measurement.rank, measurement.afterSourceCursor);
    const previous = this.measurements.get(key)?.correction ?? 0;
    const correction = measurement.measuredHeight - measurement.estimatedHeight;
    const change = correction - previous;
    this.measurements.set(key, {
      rank: measurement.rank,
      afterSourceCursor: copyCursor(measurement.afterSourceCursor),
      correction,
    });

    const scrollAdjustment = this.correctionPrecedesAnchor(
      measurement.rank,
      measurement.afterSourceCursor,
    )
      ? change
      : 0;
    return this.measurementResult(true, correction, scrollAdjustment);
  }

  // Evict a half-open rank range; already-issued measurements become stale.
  evictMeasurements(fromRank: number, toRankExclusive: number): TranscriptViewportUpdate {
    validateRank(fromRank, this.geometry.entryCount, true);
    validateRank(toRankExclusive, this.geometry.entryCount, true);
    if (toRankExclusive < fromRank) throw new TypeError("Invalid measurement eviction range.");

    let removedBeforeAnchor = 0;
    for (const [key, measurement] of this.measurements) {
      if (measurement.rank < fromRank || measurement.rank >= toRankExclusive) continue;
      if (this.correctionPrecedesAnchor(measurement.rank, measurement.afterSourceCursor))
        removedBeforeAnchor += measurement.correction;
      this.measurements.delete(key);
    }

    if (toRankExclusive > fromRank) this.generation++;
    return this.updateResult(-removedBeforeAnchor);
  }

  estimatedPrefixHeight(rank: number): number {
    validateRank(rank, this.geometry.entryCount, true);
    return basePrefixHeight(this.geometry, rank) + this.correctionBeforeRank(rank);
  }

  locateAtFraction(fraction: number): WeightedPositionLookup {
    if (!Number.isFinite(fraction)) throw new TypeError("Position fraction must be finite.");
    const clampedFraction = Math.min(1, Math.max(0, fraction));
    const totalHeight = this.correctedTotalHeight();
    const position = clampedFraction * totalHeight;
    const points = this.correctedGeometryPoints();

    if (this.geometry.entryCount === 0)
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
    const estimatedRank = Math.min(
      this.geometry.entryCount,
      Math.max(0, lower.rank + ratio * rankSpan),
    );
    const rankHint = Math.min(this.geometry.entryCount - 1, Math.floor(estimatedRank));
    const around = lower.position ?? upper.position;
    return {
      estimatedRank,
      rankHint,
      around: around === null ? null : copyEntry(around.entry),
      heightAfterAround:
        around === null ? position : position - this.estimatedPrefixHeight(around.rank),
      estimatedHeight: totalHeight,
    };
  }

  private correctedTotalHeight(): number {
    let correction = 0;
    for (const measurement of this.measurements.values()) correction += measurement.correction;
    return Math.max(0, this.geometry.estimatedTotalHeight + correction);
  }

  private correctionBeforeRank(rank: number): number {
    let correction = 0;
    for (const measurement of this.measurements.values())
      if (measurement.rank < rank) correction += measurement.correction;
    return correction;
  }

  private correctionBeforeAnchor(anchor: TranscriptViewportAnchor | null): number {
    if (anchor === null) return 0;

    let correction = 0;
    for (const measurement of this.measurements.values())
      if (this.correctionPrecedesAnchor(measurement.rank, measurement.afterSourceCursor, anchor))
        correction += measurement.correction;
    return correction;
  }

  private correctionPrecedesAnchor(
    rank: number,
    afterSourceCursor: TranscriptSourceCursor | null,
    anchor = this.anchor,
  ): boolean {
    if (anchor === null) return false;
    if (rank < anchor.rank) return true;
    if (rank > anchor.rank || afterSourceCursor === null || anchor.sourceCursor === null)
      return false;
    return compareCursor(afterSourceCursor, anchor.sourceCursor) <= 0;
  }

  private matchesKnownEntry(entry: TranscriptEntryRef, rank: number): boolean {
    const position = this.geometry.weightedPositions.find((item) => item.rank === rank);
    return position === undefined || sameEntry(position.entry, entry);
  }

  private correctedGeometryPoints(): GeometryPoint[] {
    const points = baseGeometryPoints(this.geometry);
    let previousHeight = 0;
    return points.map((point) => {
      const height = Math.max(previousHeight, point.height + this.correctionBeforeRank(point.rank));
      previousHeight = height;
      return { ...point, height };
    });
  }

  private updateResult(scrollAdjustment: number): TranscriptViewportUpdate {
    return {
      anchor: copyAnchor(this.anchor),
      followsLatest: this.followsLatest,
      scrollAdjustment,
      latestPosition: this.followsLatest ? this.correctedTotalHeight() : null,
      generation: this.generation,
    };
  }

  private measurementResult(
    accepted: boolean,
    correction: number,
    scrollAdjustment: number,
  ): TranscriptMeasurementResult {
    return { accepted, correction, scrollAdjustment, generation: this.generation };
  }
}

function basePrefixHeight(geometry: TranscriptViewportGeometry, rank: number): number {
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

function copyAndValidateGeometry(geometry: TranscriptViewportGeometry): TranscriptViewportGeometry {
  if (
    !Number.isSafeInteger(geometry.historyRevision) ||
    geometry.historyRevision < 0 ||
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

function validAnchor(
  anchor: TranscriptViewportAnchor | null,
  geometry: TranscriptViewportGeometry,
): TranscriptViewportAnchor | null {
  if (anchor === null) return null;
  if (!isValidAnchor(anchor, geometry))
    throw new TypeError("Fallback anchor does not belong to the new history revision.");
  return copyAnchor(anchor);
}

function isValidAnchor(
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

function validateMeasurement(measurement: TranscriptMeasurement): void {
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

function validateRank(rank: number, maximum: number, allowEnd: boolean): void {
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

function compareCursor(left: TranscriptSourceCursor, right: TranscriptSourceCursor): number {
  return left.segmentIndex - right.segmentIndex || left.utf8Offset - right.utf8Offset;
}

function measurementKey(rank: number, cursor: TranscriptSourceCursor | null): string {
  return cursor === null ? `${rank}:end` : `${rank}:${cursor.segmentIndex}:${cursor.utf8Offset}`;
}

function copyCursor(cursor: TranscriptSourceCursor | null): TranscriptSourceCursor | null {
  return cursor === null ? null : { ...cursor };
}

function copyEntry(entry: TranscriptEntryRef): TranscriptEntryRef {
  return { ...entry };
}

function copyAnchor(anchor: TranscriptViewportAnchor | null): TranscriptViewportAnchor | null {
  return anchor === null
    ? null
    : { ...anchor, entry: copyEntry(anchor.entry), sourceCursor: copyCursor(anchor.sourceCursor) };
}

function sameEntry(left: TranscriptEntryRef, right: TranscriptEntryRef): boolean {
  return (
    left.executionId === right.executionId &&
    left.entryId === right.entryId &&
    left.kind === right.kind &&
    left.historyRevision === right.historyRevision
  );
}
