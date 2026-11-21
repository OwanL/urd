import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { RefObject } from "preact";
import type { TranscriptPositionWindow } from "../../../sessions/transcript/Messages.ts";
import type { TranscriptViewport } from "./TranscriptViewport.ts";
import { entryKey, positionedRows, visibleRows, type PositionedRow } from "./TranscriptRows.ts";
import { bottomPadding, topPadding } from "./TranscriptScrollLayout.ts";

interface MountedRowsOptions {
  window: TranscriptPositionWindow | null;
  model: TranscriptViewport | null;
  scrollport: HTMLElement | null;
  suspended: boolean;
  scrollAdjustment: number;
  tailHeight: number;
}

export function useTranscriptRows(
  scroll: RefObject<HTMLDivElement | null>,
  historyRevision: number | null,
) {
  const retainedRows = useRef(new Map<string, PositionedRow>());
  const rowRecords = useRef(new Map<string, PositionedRow>());
  const retainedViewportRows = useRef<PositionedRow[]>([]);
  const retainedScrollport = useRef({ top: 0, width: 0 });
  const [retainedKeys, setRetainedKeys] = useState<ReadonlySet<string>>(new Set());
  const latestHistoryRevision = useRef(historyRevision);
  latestHistoryRevision.current = historyRevision;

  const lookupRow = useCallback((key: string) => rowRecords.current.get(key), []);
  const getRetainedRows = useCallback(() => retainedRows.current.values(), []);
  const lastScrollport = useCallback(() => retainedScrollport.current, []);
  const updateSelection = useCallback(() => {
    const node = scroll.current;
    if (!node) return;
    const revision = latestHistoryRevision.current;
    const selection = node.ownerDocument.getSelection();
    const next = new Map<string, PositionedRow>();
    if (selection && !selection.isCollapsed && selection.rangeCount > 0) {
      const start =
        selection.anchorNode instanceof Element
          ? selection.anchorNode.closest<HTMLElement>("[data-transcript-entry]")
          : selection.anchorNode?.parentElement?.closest<HTMLElement>("[data-transcript-entry]");
      const end =
        selection.focusNode instanceof Element
          ? selection.focusNode.closest<HTMLElement>("[data-transcript-entry]")
          : selection.focusNode?.parentElement?.closest<HTMLElement>("[data-transcript-entry]");
      const startRank = Number(start?.dataset.entryRank);
      const endRank = Number(end?.dataset.entryRank);
      if (start && end && node.contains(start) && node.contains(end)) {
        const minimum = Math.min(startRank, endRank);
        const maximum = Math.max(startRank, endRank);
        for (const [key, row] of rowRecords.current)
          if (
            row.descriptor.entry.historyRevision === revision &&
            row.rank >= minimum &&
            row.rank <= maximum
          )
            next.set(key, row);
      }
    }
    const active =
      node.ownerDocument.activeElement?.closest<HTMLElement>("[data-transcript-entry]");
    if (active && node.contains(active)) {
      const key = active.dataset.entryKey;
      const row = key ? rowRecords.current.get(key) : undefined;
      if (key && row?.descriptor.entry.historyRevision === revision) next.set(key, row);
    }
    retainedRows.current = next;
    setRetainedKeys(new Set(next.keys()));
  }, [scroll]);

  function prepareMountedRows({
    window,
    model,
    scrollport: node,
    suspended,
    scrollAdjustment,
    tailHeight,
  }: MountedRowsOptions): PositionedRow[] {
    const windowRows = window ? positionedRows(window) : [];
    const allRows = new Map<string, PositionedRow>();
    for (const row of windowRows) allRows.set(entryKey(row.descriptor.entry), row);
    for (const [key, row] of retainedRows.current) if (!allRows.has(key)) allRows.set(key, row);
    rowRecords.current = allRows;

    let visibleEntries: PositionedRow[] = [];
    if (model && node && window) {
      // Hidden scrollports have no layout box; retain the last bounded window.
      if (suspended || node.clientHeight === 0) visibleEntries = retainedViewportRows.current;
      else {
        retainedScrollport.current = { top: node.scrollTop, width: node.clientWidth };
        const snapshot = model.snapshot();
        const maximum = Math.max(
          0,
          snapshot.correctedTotalHeight +
            topPadding +
            bottomPadding +
            tailHeight -
            node.clientHeight,
        );
        // Account for height corrections before the browser clamps its scroll offset.
        const nextTop = snapshot.followsLatest
          ? maximum
          : Math.max(0, Math.min(maximum, node.scrollTop + scrollAdjustment));
        visibleEntries = retainedViewportRows.current = visibleRows(
          windowRows,
          model,
          nextTop,
          node.clientHeight,
          retainedKeys,
          topPadding,
        );
      }
    }
    const selectedRows = [...retainedRows.current.values()].filter(
      (row) =>
        !visibleEntries.some(
          (visibleRow) => entryKey(visibleRow.descriptor.entry) === entryKey(row.descriptor.entry),
        ),
    );
    return [...visibleEntries, ...selectedRows].sort((left, right) => left.rank - right.rank);
  }

  return { lookupRow, getRetainedRows, lastScrollport, updateSelection, prepareMountedRows };
}

export function useTranscriptSelectionEvents(updateSelection: () => void) {
  useEffect(() => {
    document.addEventListener("selectionchange", updateSelection);
    document.addEventListener("focusin", updateSelection);
    document.addEventListener("focusout", updateSelection);
    return () => {
      document.removeEventListener("selectionchange", updateSelection);
      document.removeEventListener("focusin", updateSelection);
      document.removeEventListener("focusout", updateSelection);
    };
  }, [updateSelection]);
}
