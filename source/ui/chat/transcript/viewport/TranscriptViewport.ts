import type { TranscriptEntryRef } from "../../../sessions/transcript/Messages.ts";
import { TranscriptMeasurements } from "./TranscriptMeasurements.ts";
import {
  basePrefixHeight,
  copyAndValidateGeometry,
  copyAnchor,
  isValidAnchor,
  locateAtFraction,
  sameEntry,
  validAnchor,
  validateMeasurement,
  validateRank,
} from "./TranscriptViewportGeometry.ts";

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
  // Monotonic within a history revision; independent from local measurement invalidation.
  readonly positionVersion: number;
  readonly entryCount: number;
  readonly estimatedTotalHeight: number;
  readonly weightedPositions: readonly WeightedEntryPosition[];
}

export interface TranscriptViewportSnapshot {
  readonly historyRevision: number;
  readonly positionVersion: number;
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

export class TranscriptViewport {
  private geometry: TranscriptViewportGeometry;
  private followsLatest = true;
  private anchor: TranscriptViewportAnchor | null = null;
  private width: number | null = null;
  private generation = 0;
  private readonly measurements = new TranscriptMeasurements();

  constructor(geometry: TranscriptViewportGeometry) {
    this.geometry = copyAndValidateGeometry(geometry);
  }

  snapshot(): TranscriptViewportSnapshot {
    return {
      historyRevision: this.geometry.historyRevision,
      positionVersion: this.geometry.positionVersion,
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
    if (!revisionChanged && next.positionVersion <= this.geometry.positionVersion)
      return this.updateResult(0);

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

  // Replace sparse lookup samples from another window of the same indexed state.
  // Measurements and follow intent remain valid because the source version did not change.
  refreshGeometryWindow(geometry: TranscriptViewportGeometry): TranscriptViewportUpdate {
    const next = copyAndValidateGeometry(geometry);
    if (
      next.historyRevision !== this.geometry.historyRevision ||
      next.positionVersion !== this.geometry.positionVersion ||
      next.entryCount !== this.geometry.entryCount ||
      next.estimatedTotalHeight !== this.geometry.estimatedTotalHeight
    )
      throw new TypeError("A refreshed geometry window must describe the same position state.");

    for (const position of next.weightedPositions) {
      const existing = this.geometry.weightedPositions.find((item) => item.rank === position.rank);
      if (
        existing &&
        (!sameEntry(existing.entry, position.entry) ||
          existing.estimatedTop !== position.estimatedTop)
      )
        throw new TypeError(
          "Transcript geometry changed a position without a new position version.",
        );
    }

    const previousAnchorTop =
      this.anchor === null ? null : this.estimatedPrefixHeight(this.anchor.rank);
    this.geometry = next;
    const scrollAdjustment =
      previousAnchorTop === null || this.anchor === null
        ? 0
        : this.estimatedPrefixHeight(this.anchor.rank) - previousAnchorTop;
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

    // A scrollbar seek may land in an unloaded gap before an indexed anchor arrives.
    if (anchor !== null && !isValidAnchor(anchor, this.geometry)) return false;
    this.followsLatest = false;
    this.anchor = anchor === null ? null : copyAnchor(anchor);
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

    const removedCorrection = this.measurements.beforeAnchor(this.anchor);
    this.width = width;
    this.measurements.clear();
    this.generation++;
    return this.updateResult(-removedCorrection);
  }

  // Use after source/layout changes that make otherwise same-width measurements stale.
  invalidateMeasurements(): TranscriptViewportUpdate {
    const removedCorrection = this.measurements.beforeAnchor(this.anchor);
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

    const { correction, scrollAdjustment } = this.measurements.record(measurement, this.anchor);
    return this.measurementResult(true, correction, scrollAdjustment);
  }

  // Evict a half-open rank range; already-issued measurements become stale.
  evictMeasurements(fromRank: number, toRankExclusive: number): TranscriptViewportUpdate {
    validateRank(fromRank, this.geometry.entryCount, true);
    validateRank(toRankExclusive, this.geometry.entryCount, true);
    if (toRankExclusive < fromRank) throw new TypeError("Invalid measurement eviction range.");

    const removedBeforeAnchor = this.measurements.evict(fromRank, toRankExclusive, this.anchor);

    if (toRankExclusive > fromRank) this.generation++;
    return this.updateResult(-removedBeforeAnchor);
  }

  estimatedPrefixHeight(rank: number): number {
    validateRank(rank, this.geometry.entryCount, true);
    return basePrefixHeight(this.geometry, rank) + this.measurements.beforeRank(rank);
  }

  positionFractionAtPixel(pixel: number): number {
    if (!Number.isFinite(pixel)) throw new TypeError("Pixel position must be finite.");
    const totalHeight = this.correctedTotalHeight();
    if (totalHeight <= 0 || this.geometry.estimatedTotalHeight <= 0 || pixel <= 0) return 0;
    if (pixel >= totalHeight) return 1;
    const clamped = Math.max(0, Math.min(totalHeight, pixel));
    const location = this.locateAtFraction(clamped / totalHeight);
    const sourceHeight = clamped - this.measurements.beforeRank(location.rankHint);
    return Math.max(0, Math.min(1, sourceHeight / this.geometry.estimatedTotalHeight));
  }

  locateAtFraction(fraction: number): WeightedPositionLookup {
    if (!Number.isFinite(fraction)) throw new TypeError("Position fraction must be finite.");
    return locateAtFraction(this.geometry, fraction, this.correctedTotalHeight(), (rank) =>
      this.measurements.beforeRank(rank),
    );
  }

  private correctedTotalHeight(): number {
    return Math.max(0, this.geometry.estimatedTotalHeight + this.measurements.total());
  }

  private matchesKnownEntry(entry: TranscriptEntryRef, rank: number): boolean {
    const position = this.geometry.weightedPositions.find((item) => item.rank === rank);
    return position === undefined || sameEntry(position.entry, entry);
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
