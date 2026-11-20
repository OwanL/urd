import type { TranscriptPositionWindow } from "../../../sessions/transcript/Messages.ts";
import type { TranscriptNavigationState } from "../navigation/TranscriptNavigationStorage.ts";
import { sameEntry, type PositionedRow } from "./TranscriptRows.ts";
import { layoutProfile, topPadding } from "./TranscriptScrollLayout.ts";
import type {
  TranscriptViewport,
  TranscriptViewportAnchor,
  TranscriptViewportGeometry,
} from "./TranscriptViewport.ts";

export interface WindowAnchorRestore {
  restoreAnchor(anchor: TranscriptNavigationState["anchor"]): void;
  cancelWidthRestore(): void;
  discardRestoreForOtherRevision(revision: number): void;
}

// A null result leaves the current window in place; accepted results describe the scroll correction.
export function applyTranscriptWindow(
  model: TranscriptViewport,
  geometry: TranscriptViewportGeometry,
  next: TranscriptPositionWindow,
  seekFraction: number,
  scrollport: HTMLElement | null,
  restore: WindowAnchorRestore,
): { adjustment: number; resetAdjustment: boolean } | null {
  const previous = model.snapshot();
  if (
    geometry.historyRevision < previous.historyRevision ||
    (geometry.historyRevision === previous.historyRevision &&
      geometry.positionVersion < previous.positionVersion)
  )
    return null;

  const revisionChanged = geometry.historyRevision !== previous.historyRevision;
  if (revisionChanged) restore.cancelWidthRestore();
  const fallbackTarget =
    revisionChanged && !previous.followsLatest ? (next.target ?? next.entries[0]?.position) : null;
  const fallback: TranscriptViewportAnchor | null = fallbackTarget
    ? {
        entry: fallbackTarget,
        rank: fallbackTarget.rank,
        sourceCursor: null,
        viewportOffset:
          topPadding +
          fallbackTarget.prefixWeight * layoutProfile.pixelsPerWeight -
          (scrollport?.scrollTop ?? 0),
      }
    : null;
  const update = revisionChanged
    ? model.updateGeometry(geometry, fallback)
    : geometry.positionVersion > previous.positionVersion
      ? model.updateGeometry(geometry)
      : model.refreshGeometryWindow(geometry);
  if (revisionChanged) restore.discardRestoreForOtherRevision(geometry.historyRevision);

  if (seekFraction >= 0.999 && previous.followsLatest) {
    model.jumpToLatest();
    return { adjustment: update.scrollAdjustment, resetAdjustment: true };
  }
  if (next.target && !revisionChanged) {
    const anchor = previous.anchor;
    const anchorIncluded =
      anchor !== null && next.entries.some(({ position }) => sameEntry(position, anchor.entry));
    const targetOutsideAnchor =
      !anchorIncluded && (anchor === null || Math.abs(anchor.rank - next.target.rank) > 20);
    if (targetOutsideAnchor) {
      restore.cancelWidthRestore();
      const viewportOffset = scrollport
        ? topPadding + model.estimatedPrefixHeight(next.target.rank) - scrollport.scrollTop
        : 0;
      model.recordAnchor({
        entry: next.target,
        rank: next.target.rank,
        sourceCursor: null,
        viewportOffset,
      });
      return { adjustment: 0, resetAdjustment: true };
    }
  }
  return { adjustment: update.scrollAdjustment, resetAdjustment: revisionChanged };
}

export function evictMeasurementsOutsideWindow(
  model: TranscriptViewport,
  next: TranscriptPositionWindow,
  retained: Iterable<PositionedRow>,
): number {
  const entryCount = next.state.entryCount;
  if (entryCount === 0 || model.snapshot().measurementCount === 0) return 0;
  const keep = new Set(next.entries.map(({ position }) => position.rank));
  for (const row of retained) keep.add(row.rank);
  const sorted = [...keep].sort((left, right) => left - right);
  let from = 0;
  let adjustment = 0;
  for (const rank of sorted) {
    if (rank > from) adjustment += model.evictMeasurements(from, rank).scrollAdjustment;
    from = rank + 1;
  }
  if (from < entryCount) adjustment += model.evictMeasurements(from, entryCount).scrollAdjustment;
  return adjustment;
}
