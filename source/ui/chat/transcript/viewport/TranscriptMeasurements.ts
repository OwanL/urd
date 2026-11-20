import type {
  TranscriptMeasurement,
  TranscriptSourceCursor,
  TranscriptViewportAnchor,
} from "./TranscriptViewport.ts";
import { compareCursor, copyCursor, measurementKey } from "./TranscriptViewportGeometry.ts";

interface HeightCorrection {
  readonly rank: number;
  readonly afterSourceCursor: TranscriptSourceCursor | null;
  readonly correction: number;
}

export class TranscriptMeasurements {
  private readonly measurements = new Map<string, HeightCorrection>();

  get size(): number {
    return this.measurements.size;
  }

  clear(): void {
    this.measurements.clear();
  }

  record(
    measurement: TranscriptMeasurement,
    anchor: TranscriptViewportAnchor | null,
  ): { correction: number; scrollAdjustment: number } {
    const key = measurementKey(measurement.rank, measurement.afterSourceCursor);
    const previous = this.measurements.get(key)?.correction ?? 0;
    const correction = measurement.measuredHeight - measurement.estimatedHeight;
    const change = correction - previous;
    this.measurements.set(key, {
      rank: measurement.rank,
      afterSourceCursor: copyCursor(measurement.afterSourceCursor),
      correction,
    });
    const scrollAdjustment = this.precedesAnchor(
      measurement.rank,
      measurement.afterSourceCursor,
      anchor,
    )
      ? change
      : 0;
    return { correction, scrollAdjustment };
  }

  evict(
    fromRank: number,
    toRankExclusive: number,
    anchor: TranscriptViewportAnchor | null,
  ): number {
    let removedBeforeAnchor = 0;
    for (const [key, measurement] of this.measurements) {
      if (measurement.rank < fromRank || measurement.rank >= toRankExclusive) continue;
      if (this.precedesAnchor(measurement.rank, measurement.afterSourceCursor, anchor))
        removedBeforeAnchor += measurement.correction;
      this.measurements.delete(key);
    }
    return removedBeforeAnchor;
  }

  total(): number {
    let correction = 0;
    for (const measurement of this.measurements.values()) correction += measurement.correction;
    return correction;
  }

  beforeRank(rank: number): number {
    let correction = 0;
    for (const measurement of this.measurements.values())
      if (measurement.rank < rank) correction += measurement.correction;
    return correction;
  }

  beforeAnchor(anchor: TranscriptViewportAnchor | null): number {
    if (anchor === null) return 0;
    let correction = 0;
    for (const measurement of this.measurements.values())
      if (this.precedesAnchor(measurement.rank, measurement.afterSourceCursor, anchor))
        correction += measurement.correction;
    return correction;
  }

  private precedesAnchor(
    rank: number,
    afterSourceCursor: TranscriptSourceCursor | null,
    anchor: TranscriptViewportAnchor | null,
  ): boolean {
    if (anchor === null) return false;
    if (rank < anchor.rank) return true;
    if (rank > anchor.rank || afterSourceCursor === null || anchor.sourceCursor === null)
      return false;
    return compareCursor(afterSourceCursor, anchor.sourceCursor) <= 0;
  }
}
