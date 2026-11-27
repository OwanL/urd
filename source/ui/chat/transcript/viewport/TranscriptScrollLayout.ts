import { visibleEntryElements, type PositionedRow } from "./TranscriptRows.ts";
import type { TranscriptViewport } from "./TranscriptViewport.ts";

export const layoutProfile = { version: 1 as const, pixelsPerWeight: 0.1 };
export const topPadding = 16;
export const bottomPadding = 28;

export interface RetainedScrollPosition {
  readonly top: number;
  readonly width: number;
}

export interface TranscriptDraftAnchor {
  readonly row: HTMLElement;
  readonly viewportOffset: number;
}

export function captureTranscriptDraftAnchor(root: HTMLElement): TranscriptDraftAnchor[] | null {
  const viewport = root.getBoundingClientRect();
  const anchors: TranscriptDraftAnchor[] = [];
  for (const row of root.querySelectorAll<HTMLElement>(".transcript-entry-draft")) {
    const bounds = row.getBoundingClientRect();
    if (bounds.bottom > viewport.top && bounds.top < viewport.bottom)
      anchors.push({ row, viewportOffset: bounds.top - viewport.top });
  }
  return anchors.length ? anchors : null;
}

export function restoreTranscriptDraftAnchor(
  root: HTMLElement,
  anchors: readonly TranscriptDraftAnchor[],
): number | null {
  const anchor = anchors.find(({ row }) => root.contains(row));
  if (!anchor) return null;
  const viewportOffset = anchor.row.getBoundingClientRect().top - root.getBoundingClientRect().top;
  const target = root.scrollTop + viewportOffset - anchor.viewportOffset;
  if (target === root.scrollTop) return null;
  root.scrollTop = target;
  return root.scrollTop;
}

// Returns the actual scroll target after the browser clamps it, or null if untouched.
export function alignTranscriptScrollport(
  node: HTMLElement,
  model: TranscriptViewport,
  adjustment: number | null,
  retained: RetainedScrollPosition | null,
  hasPendingRestore: () => boolean,
): number | null {
  let target: number | null = null;
  if (adjustment !== null) {
    if (model.snapshot().followsLatest) {
      node.scrollTop = Math.max(0, node.scrollHeight - node.clientHeight);
    } else if (adjustment !== 0) node.scrollTop += adjustment;
    target = node.scrollTop;
  }
  const snapshot = model.snapshot();
  if (retained && !snapshot.followsLatest && retained.width === node.clientWidth) {
    node.scrollTop = retained.top;
    target = node.scrollTop;
  }
  if (snapshot.followsLatest) {
    node.scrollTop = Math.max(0, node.scrollHeight - node.clientHeight);
    target = node.scrollTop;
  } else if (snapshot.anchor) {
    const top = topPadding + model.estimatedPrefixHeight(snapshot.anchor.rank);
    if (snapshot.anchor.sourceCursor === null) {
      node.scrollTop = Math.max(0, top - snapshot.anchor.viewportOffset);
      target = node.scrollTop;
    } else if (hasPendingRestore()) {
      node.scrollTop = Math.max(0, top);
      target = node.scrollTop;
    }
  }
  return target;
}

// Record an immediate row anchor before the async Markdown source capture runs.
export function recordTranscriptUserScroll(
  node: HTMLElement,
  model: TranscriptViewport,
  lookupRow: (key: string) => PositionedRow | undefined,
): boolean {
  const atLatest = node.scrollHeight - node.scrollTop - node.clientHeight < 40;
  const portTop = node.getBoundingClientRect().top;
  const first = visibleEntryElements(node)[0];
  const key = first?.dataset.entryKey;
  const row = key ? lookupRow(key) : undefined;
  const offset = first ? first.getBoundingClientRect().top - portTop : 0;
  if (atLatest) return model.recordUserScroll(null, true);
  if (!row) return model.recordUserScroll(null, false);
  return model.recordUserScroll(
    { entry: row.descriptor.entry, rank: row.rank, sourceCursor: null, viewportOffset: offset },
    false,
  );
}

export function transcriptScrollFraction(node: HTMLElement, model: TranscriptViewport): number {
  return model.snapshot().followsLatest
    ? 1
    : model.positionFractionAtPixel(node.scrollTop + node.clientHeight / 2 - topPadding);
}
