import type {
  TranscriptEntryDescriptor,
  TranscriptEntryRef,
  TranscriptPositionWindow,
  ToolCallLabel,
} from "../../../sessions/transcript/Messages.ts";
import { toolCallLabel } from "../tool-calls/ToolRowPresentation.ts";
import type { TranscriptViewport } from "./TranscriptViewport.ts";

export interface PositionedRow {
  readonly descriptor: TranscriptEntryDescriptor;
  readonly rank: number;
  readonly toolLabel: ToolCallLabel | null;
}

export function entryKey(entry: TranscriptEntryRef): string {
  return `${entry.executionId}/${entry.entryId}/${entry.kind}/${entry.historyRevision}`;
}

export function sameEntry(left: TranscriptEntryRef, right: TranscriptEntryRef): boolean {
  return (
    left.executionId === right.executionId &&
    left.entryId === right.entryId &&
    left.kind === right.kind &&
    left.historyRevision === right.historyRevision
  );
}

export function positionedRows(window: TranscriptPositionWindow): PositionedRow[] {
  return window.entries.map(({ descriptor, position }) => ({
    descriptor,
    rank: position.rank,
    toolLabel: descriptor.tool ? toolCallLabel(descriptor.tool, window.toolLabels) : null,
  }));
}

export function windowCoversFraction(window: TranscriptPositionWindow, fraction: number): boolean {
  const entries = window.entries;
  if (entries.length === 0 || window.state.totalWeight <= 0) return true;
  const first = entries[0]!.position;
  const last = entries.at(-1)!.position;
  const averageWeight = window.state.totalWeight / Math.max(1, window.state.entryCount);
  const edge = Math.min(4_096, Math.max(256, averageWeight * 2));
  const offset = fraction * window.state.totalWeight;
  const before = window.hasBefore && offset < first.prefixWeight + edge;
  const after = window.hasAfter && offset > last.prefixWeight + last.weight - edge;
  return !before && !after;
}

export function visibleRows(
  rows: readonly PositionedRow[],
  viewport: TranscriptViewport,
  scrollTop: number,
  viewportHeight: number,
  retainedKeys: ReadonlySet<string>,
  topPadding: number,
): PositionedRow[] {
  const from = Math.max(0, scrollTop - viewportHeight);
  const to = scrollTop + viewportHeight * 2;
  return rows.filter((row) => {
    if (retainedKeys.has(entryKey(row.descriptor.entry))) return true;
    const top = topPadding + viewport.estimatedPrefixHeight(row.rank);
    const bottom = topPadding + viewport.estimatedPrefixHeight(row.rank + 1);
    return bottom > from && top < to;
  });
}
