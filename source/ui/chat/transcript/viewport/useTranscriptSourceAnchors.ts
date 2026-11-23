import { useCallback, useMemo, useRef, useState } from "preact/hooks";
import type { RefObject } from "preact";
import type { MarkdownSavedSourcePosition } from "../../../markdown-rendering/MarkdownMessageView.tsx";
import type { TranscriptTextFacts } from "../../../sessions/transcript/Messages.ts";
import type { TranscriptNavigationState } from "../navigation/TranscriptNavigationStorage.ts";
import type { RestoreSource } from "../VirtualEntry.tsx";
import { entryKey, sameEntry, visibleEntryElements, type PositionedRow } from "./TranscriptRows.ts";
import type {
  TranscriptSourceCursor,
  TranscriptViewport,
  TranscriptViewportAnchor,
} from "./TranscriptViewport.ts";
import type { WindowAnchorRestore } from "./TranscriptWindowApplication.ts";

type SourceCapture = (signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>;
type SavedAnchor = NonNullable<TranscriptNavigationState["anchor"]>;

interface PendingWidthRestore {
  readonly key: string;
  readonly width: number;
  readonly saved: MarkdownSavedSourcePosition;
}

interface SourceAnchorOptions {
  scroll: RefObject<HTMLDivElement | null>;
  viewport: RefObject<TranscriptViewport | null>;
  resumingVisibility: boolean;
  lookupRow: (key: string) => PositionedRow | undefined;
  isSuspended: () => boolean;
  requestLayout: () => void;
  onProgrammaticScroll: (top: number) => void;
  onAnchorRecorded: () => void;
}

// Coordinates registered Markdown rows with the model's reading anchor and width changes.
export function useTranscriptSourceAnchors({
  scroll,
  viewport,
  resumingVisibility,
  lookupRow,
  isSuspended,
  requestLayout,
  onProgrammaticScroll,
  onAnchorRecorded,
}: SourceAnchorOptions) {
  const [pendingRestore, setPendingRestore] = useState<SavedAnchor | null>(null);
  const pendingRestoreRef = useRef<SavedAnchor | null>(null);
  const pendingWidthRestore = useRef<PendingWidthRestore | null>(null);
  const captures = useRef(new Map<string, SourceCapture>());
  const restores = useRef(new Map<string, RestoreSource>());
  const sourceRequest = useRef<AbortController | null>(null);
  const captureFrame = useRef<number | null>(null);
  const widthGeneration = useRef(0);

  if (resumingVisibility && pendingRestoreRef.current === null) {
    const snapshot = viewport.current?.snapshot();
    if (snapshot && !snapshot.followsLatest && snapshot.anchor?.sourceCursor)
      pendingRestoreRef.current = snapshot.anchor;
  }

  const restoreAnchor = useCallback((anchor: SavedAnchor | null) => {
    pendingRestoreRef.current = anchor;
    setPendingRestore(anchor);
  }, []);
  const clearRestore = useCallback(() => {
    pendingWidthRestore.current = null;
    restoreAnchor(null);
  }, [restoreAnchor]);
  const cancelWidthRestore = useCallback(() => {
    if (pendingWidthRestore.current !== null) clearRestore();
  }, [clearRestore]);
  const discardRestoreForOtherRevision = useCallback(
    (revision: number) => {
      if (pendingRestoreRef.current?.entry.historyRevision !== revision) restoreAnchor(null);
    },
    [restoreAnchor],
  );
  const hasPendingRestore = useCallback(() => pendingRestoreRef.current !== null, []);

  const resizeViewport = useCallback(
    (model: TranscriptViewport, width: number) => {
      const before = model.snapshot();
      const anchor = before.followsLatest ? null : before.anchor;
      const key = anchor ? entryKey(anchor.entry) : null;
      const row = key ? lookupRow(key) : undefined;
      const saved = anchor && row ? savedSourcePosition(anchor, row) : null;
      widthGeneration.current++;
      const update = model.setWidth(width);
      const hadPendingRestore = pendingWidthRestore.current !== null;
      pendingWidthRestore.current = saved && key ? { key, width, saved } : null;
      if (pendingWidthRestore.current && anchor) restoreAnchor(anchor);
      else if (hadPendingRestore) restoreAnchor(null);
      return update.scrollAdjustment;
    },
    [lookupRow, restoreAnchor],
  );
  const applyWidthRestore = useCallback(
    (node: HTMLElement, onScroll: (scrollTop: number) => void) => {
      const pending = pendingWidthRestore.current;
      if (!pending || node.clientWidth !== pending.width) return;
      const restore = restores.current.get(pending.key);
      if (!restore) return;
      const priorScrollTop = node.scrollTop;
      const restored = restore(pending.saved);
      if (node.scrollTop !== priorScrollTop) onScroll(node.scrollTop);
      if (restored && pendingWidthRestore.current === pending) clearRestore();
    },
    [clearRestore],
  );

  const onSourceCapture = useCallback((key: string, capture: SourceCapture | null) => {
    if (capture) captures.current.set(key, capture);
    else captures.current.delete(key);
  }, []);
  const onSourceRestore = useCallback(
    (key: string, restore: RestoreSource | null) => {
      if (restore) restores.current.set(key, restore);
      else restores.current.delete(key);
      if (pendingWidthRestore.current?.key === key) requestLayout();
    },
    [requestLayout],
  );

  const abortCapture = useCallback(() => {
    sourceRequest.current?.abort();
    sourceRequest.current = null;
  }, []);
  const interruptForUserScroll = useCallback(() => {
    if (pendingWidthRestore.current !== null || pendingRestoreRef.current !== null) clearRestore();
    abortCapture();
  }, [abortCapture, clearRestore]);

  const captureVisibleAnchor = useCallback(async () => {
    const node = scroll.current;
    const model = viewport.current;
    if (!node || !model || isSuspended() || hasPendingRestore() || node.clientHeight === 0) return;
    const initial = model.snapshot();
    if (initial.followsLatest || node.clientWidth !== initial.width) return;
    const captureWidthGeneration = widthGeneration.current;
    const portTop = node.getBoundingClientRect().top;
    const elements = visibleEntryElements(node);
    const first = elements[0];
    if (!first?.dataset.entryKey) return;
    let key = first.dataset.entryKey;
    let row = lookupRow(key);
    if (!row) return;
    let sourcePosition: MarkdownSavedSourcePosition | null = null;
    let sourceCursor: TranscriptSourceCursor | null = null;
    let offset = first.getBoundingClientRect().top - portTop;
    sourceRequest.current?.abort();
    const controller = new AbortController();
    sourceRequest.current = controller;
    try {
      // A row's bottom padding may be visible while all of its text is clipped.
      for (const element of elements) {
        const candidateKey = element.dataset.entryKey;
        if (!candidateKey) continue;
        const candidate = lookupRow(candidateKey);
        const capture = captures.current.get(candidateKey);
        if (!candidate?.descriptor.text || !capture) continue;
        let saved: MarkdownSavedSourcePosition | null = null;
        try {
          saved = await capture(controller.signal);
        } catch {
          // Keep the entry-top fallback if no visible source can be captured.
        }
        if (controller.signal.aborted) return;
        if (!saved || !sameMessage(candidate.descriptor.text, saved)) continue;
        key = candidateKey;
        row = candidate;
        sourcePosition = saved;
        sourceCursor = {
          segmentIndex: saved.cursor.segmentIndex,
          utf8Offset: saved.cursor.utf8Offset,
        };
        offset = saved.offset;
        break;
      }
    } finally {
      if (sourceRequest.current === controller) sourceRequest.current = null;
    }
    const currentRow = lookupRow(key);
    const currentSnapshot = model.snapshot();
    if (
      viewport.current !== model ||
      isSuspended() ||
      hasPendingRestore() ||
      node.clientHeight === 0 ||
      currentSnapshot.historyRevision !== initial.historyRevision ||
      !currentRow ||
      currentRow.rank !== row.rank ||
      !sameEntry(currentRow.descriptor.entry, row.descriptor.entry)
    )
      return;
    const anchor: TranscriptViewportAnchor = {
      entry: row.descriptor.entry,
      rank: row.rank,
      sourceCursor,
      viewportOffset: offset,
    };
    if (!model.recordAnchor(anchor)) return;
    if (sourcePosition && captureWidthGeneration !== widthGeneration.current) {
      const width = model.snapshot().width;
      if (width !== null && width > 0 && node.clientWidth === width) {
        pendingWidthRestore.current = { key, width, saved: sourcePosition };
        restoreAnchor(anchor);
        requestLayout();
      }
    }
    onAnchorRecorded();
  }, [
    hasPendingRestore,
    isSuspended,
    lookupRow,
    onAnchorRecorded,
    requestLayout,
    restoreAnchor,
    scroll,
    viewport,
  ]);
  const captureVisibleRef = useRef(captureVisibleAnchor);
  captureVisibleRef.current = captureVisibleAnchor;

  const scheduleSourceCapture = useCallback(() => {
    if (captureFrame.current !== null) return;
    captureFrame.current = requestAnimationFrame(() => {
      captureFrame.current = null;
      void captureVisibleRef.current().catch(() => {});
    });
  }, []);
  // Cleanup cancels the frame before the position read and aborts capture after it.
  const cancelCaptureFrame = useCallback(() => {
    if (captureFrame.current !== null) cancelAnimationFrame(captureFrame.current);
  }, []);

  const onPublished = useCallback(
    (key: string, restoredScrollTop?: number) => {
      if (restoredScrollTop !== undefined) {
        // Markdown restores a source anchor on the shared scrollport. Its scroll
        // event must not be treated as the reader opting out of following.
        onProgrammaticScroll(restoredScrollTop);
        if (viewport.current?.snapshot().followsLatest) requestLayout();
      }
      if (pendingWidthRestore.current?.key === key) {
        requestLayout();
        return;
      }
      const restore = pendingRestoreRef.current;
      if (restore && entryKey(restore.entry) === key) restoreAnchor(null);
      scheduleSourceCapture();
    },
    [onProgrammaticScroll, requestLayout, restoreAnchor, scheduleSourceCapture, viewport],
  );

  function savedPositionForRow(row: PositionedRow) {
    const wanted = pendingRestoreRef.current ?? pendingRestore;
    return (wanted && savedSourcePosition(wanted, row)) ?? undefined;
  }

  const windowRestore = useMemo<WindowAnchorRestore>(
    () => ({ restoreAnchor, cancelWidthRestore, discardRestoreForOtherRevision }),
    [restoreAnchor, cancelWidthRestore, discardRestoreForOtherRevision],
  );
  return {
    windowRestore,
    clearRestore,
    hasPendingRestore,
    resizeViewport,
    applyWidthRestore,
    rowSources: { onSourceCapture, onSourceRestore, onPublished },
    abortCapture,
    interruptForUserScroll,
    captureVisibleAnchor,
    scheduleSourceCapture,
    cancelCaptureFrame,
    savedPositionForRow,
  };
}

function savedSourcePosition(
  anchor: SavedAnchor,
  row: PositionedRow,
): MarkdownSavedSourcePosition | null {
  const text = row.descriptor.text;
  if (!anchor.sourceCursor || !text || !sameEntry(anchor.entry, row.descriptor.entry)) return null;
  return {
    cursor: {
      message: text.from.message,
      segmentIndex: anchor.sourceCursor.segmentIndex,
      utf8Offset: anchor.sourceCursor.utf8Offset,
    },
    // This is the captured source cursor's viewport offset, not cached row geometry.
    offset: anchor.viewportOffset,
  };
}

function sameMessage(text: TranscriptTextFacts, source: MarkdownSavedSourcePosition): boolean {
  const actual = source.cursor.message;
  const expected = text.from.message;
  return (
    actual.executionId === expected.executionId &&
    actual.historyRevision === expected.historyRevision &&
    actual.messageId === expected.messageId &&
    actual.role === expected.role
  );
}
