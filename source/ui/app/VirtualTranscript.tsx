import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import type { MarkdownSavedSourcePosition } from "../markdown-rendering/MarkdownMessageView.tsx";
import type {
  TranscriptEntryDescriptor,
  TranscriptEntryRef,
  TranscriptPositionWindow,
  TranscriptTextFacts,
  TranscriptToolFacts,
} from "../sessions/Messages.ts";
import { isTextEntry } from "../sessions/Messages.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type {
  TranscriptMeasurement,
  TranscriptSourceCursor,
  TranscriptViewportAnchor,
} from "../sessions/TranscriptViewport.ts";
import { TranscriptViewport as TranscriptViewportModel } from "../sessions/TranscriptViewport.ts";
import { transcriptViewportGeometry } from "../sessions/TranscriptGeometry.ts";
import type { TranscriptNavigationState } from "../sessions/TranscriptNavigationStorage.ts";
import { IconButton } from "../components/IconButton.tsx";
import {
  useTranscriptNavigation,
  transcriptNavigationProfileVersion,
} from "./useTranscriptNavigation.ts";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import "../chat/transcript.css";

const layoutProfile = { version: 1 as const, pixelsPerWeight: 0.1 };
const topPadding = 16;
const bottomPadding = 28;
const seekDebounceMs = 75;

interface VirtualTranscriptProps {
  client: SessionClient;
  display: TranscriptDisplay;
  streamId: string | null;
  sessionId: string;
  historyRevision: number | null;
  notice: string | null;
  visible: boolean;
  navigation: TranscriptNavigationState | undefined;
  onNavigationChange: (state: TranscriptNavigationState) => void;
}

interface PositionedRow {
  readonly descriptor: TranscriptEntryDescriptor;
  readonly rank: number;
}

interface PendingWidthRestore {
  readonly key: string;
  readonly width: number;
  readonly saved: MarkdownSavedSourcePosition;
}

type RestoreSource = (saved: MarkdownSavedSourcePosition) => boolean;

function entryKey(entry: TranscriptEntryRef): string {
  return `${entry.executionId}/${entry.entryId}/${entry.kind}/${entry.historyRevision}`;
}

function sameEntry(left: TranscriptEntryRef, right: TranscriptEntryRef): boolean {
  return (
    left.executionId === right.executionId &&
    left.entryId === right.entryId &&
    left.kind === right.kind &&
    left.historyRevision === right.historyRevision
  );
}

function cursorFor(saved: MarkdownSavedSourcePosition): TranscriptSourceCursor {
  return { segmentIndex: saved.cursor.segmentIndex, utf8Offset: saved.cursor.utf8Offset };
}

function savedSourcePosition(
  anchor: TranscriptViewportAnchor,
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

function positionedRows(window: TranscriptPositionWindow): PositionedRow[] {
  return window.entries.map(({ descriptor, position }) => ({
    descriptor,
    rank: position.rank,
  }));
}

function windowCoversFraction(window: TranscriptPositionWindow, fraction: number): boolean {
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

function captureMessagePosition(
  text: TranscriptTextFacts,
  source: MarkdownSavedSourcePosition,
): MarkdownSavedSourcePosition | null {
  if (
    source.cursor.message.executionId !== text.from.message.executionId ||
    source.cursor.message.historyRevision !== text.from.message.historyRevision ||
    source.cursor.message.messageId !== text.from.message.messageId ||
    source.cursor.message.role !== text.from.message.role
  )
    return null;
  return source;
}

function visibleRows(
  rows: readonly PositionedRow[],
  viewport: TranscriptViewportModel,
  scrollTop: number,
  viewportHeight: number,
  retainedKeys: ReadonlySet<string>,
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

function visibleElements(root: HTMLElement): HTMLElement[] {
  const bounds = root.getBoundingClientRect();
  return [...root.querySelectorAll<HTMLElement>("[data-transcript-entry]")].filter((row) => {
    const rect = row.getBoundingClientRect();
    return rect.bottom > bounds.top && rect.top < bounds.bottom;
  });
}

function firstVisibleRow(root: HTMLElement): HTMLElement | undefined {
  return visibleElements(root)[0];
}

function ToolEntry({ tool, completion }: { tool: TranscriptToolFacts; completion: boolean }) {
  const status =
    tool.outcome ?? (tool.started ? "Running" : tool.isClosed ? "Not started" : "Waiting");
  return (
    <div class="message message-assistant">
      <div class="message-text">
        {completion ? "Completed " : ""}
        {tool.name ?? "Tool"} · {status}
      </div>
    </div>
  );
}

function VirtualEntry({
  display,
  streamId,
  sessionId,
  descriptor,
  rank,
  top,
  root,
  paused,
  saved,
  onMeasure,
  onSourceCapture,
  onSourceRestore,
  onPublished,
}: {
  display: TranscriptDisplay;
  streamId: string | null;
  sessionId: string;
  descriptor: TranscriptEntryDescriptor;
  rank: number;
  top: number;
  root: HTMLElement | null;
  paused: boolean;
  saved: MarkdownSavedSourcePosition | undefined;
  onMeasure: (descriptor: TranscriptEntryDescriptor, rank: number, height: number) => void;
  onSourceCapture: (
    key: string,
    capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null,
  ) => void;
  onSourceRestore: (key: string, restore: RestoreSource | null) => void;
  onPublished: (key: string) => void;
}) {
  const row = useRef<HTMLDivElement>(null);
  const key = entryKey(descriptor.entry);
  const registerSource = useCallback(
    (capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null) =>
      onSourceCapture(key, capture),
    [key, onSourceCapture],
  );
  const registerRestore = useCallback(
    (restore: RestoreSource | null) => onSourceRestore(key, restore),
    [key, onSourceRestore],
  );
  const published = useCallback(() => onPublished(key), [key, onPublished]);

  useLayoutEffect(() => {
    const element = row.current;
    if (!element) return;
    const update = () => onMeasure(descriptor, rank, element.getBoundingClientRect().height);
    const observer = new ResizeObserver(update);
    observer.observe(element);
    update();
    return () => observer.disconnect();
  }, [descriptor, rank, onMeasure]);

  const role = descriptor.entry.kind === "user" ? "user" : "assistant";
  return (
    <div
      ref={row}
      class="transcript-entry"
      data-transcript-entry=""
      data-entry-key={key}
      data-entry-rank={rank}
      style={{ top: `${top}px` }}
    >
      {isTextEntry(descriptor.entry) && descriptor.text ? (
        <display.MessageBody
          display={display}
          streamId={streamId}
          sessionId={sessionId}
          entry={descriptor.entry}
          text={descriptor.text}
          root={root}
          paused={paused}
          {...(saved ? { saved } : {})}
          registerSource={registerSource}
          registerRestore={registerRestore}
          onPublished={published}
        />
      ) : descriptor.tool ? (
        <ToolEntry tool={descriptor.tool} completion={descriptor.entry.kind === "toolCompletion"} />
      ) : (
        <div class={`message message-${role}`} />
      )}
    </div>
  );
}

export function VirtualTranscript({
  client,
  display,
  streamId,
  sessionId,
  historyRevision,
  notice,
  visible,
  navigation,
  onNavigationChange,
}: VirtualTranscriptProps) {
  const scroll = useRef<HTMLDivElement>(null);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [positionWindow, setPositionWindow] = useState<TranscriptPositionWindow | null>(null);
  const positionWindowRef = useRef<TranscriptPositionWindow | null>(null);
  const viewport = useRef<TranscriptViewportModel | null>(null);
  const [modelVersion, setModelVersion] = useState(0);
  const [scrollVersion, setScrollVersion] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [documentInactive, setDocumentInactive] = useState(() => document.hidden);
  const [pendingRestore, setPendingRestore] = useState<TranscriptNavigationState["anchor"]>(null);
  const pendingRestoreRef = useRef<TranscriptNavigationState["anchor"]>(null);
  const sourceCaptures = useRef(
    new Map<string, (signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>>(),
  );
  const sourceRestores = useRef(new Map<string, RestoreSource>());
  const pendingWidthRestoreRef = useRef<PendingWidthRestore | null>(null);
  const retainedRows = useRef(new Map<string, PositionedRow>());
  const rowRecords = useRef(new Map<string, PositionedRow>());
  const [retainedKeys, setRetainedKeys] = useState<ReadonlySet<string>>(new Set());
  const activeRequest = useRef<AbortController | null>(null);
  const sourceRequest = useRef<AbortController | null>(null);
  const loadWindowRef = useRef<(fraction: number) => Promise<void>>(async () => {});
  const captureVisibleRef = useRef<() => Promise<void>>(async () => {});
  const captureFrame = useRef<number | null>(null);
  const flushNavigationRef = useRef<() => void>(() => {});
  const requestGeneration = useRef(0);
  const widthGeneration = useRef(0);
  const suspended = useRef(false);
  const initialized = useRef(false);
  const scrollAdjustment = useRef<number | null>(null);
  const programmaticScroll = useRef<number | null>(null);
  const seekTimer = useRef<number | null>(null);
  const desiredFraction = useRef(1);
  const latest = useRef({ historyRevision, visible, notice });
  latest.current = { historyRevision, visible, notice };

  const publishNavigation = useCallback(
    (state: TranscriptNavigationState) => onNavigationChange(state),
    [onNavigationChange],
  );
  const suspendReads = useCallback(() => {
    if (suspended.current) return;
    suspended.current = true;
    requestGeneration.current++;
    activeRequest.current?.abort();
    activeRequest.current = null;
    if (seekTimer.current !== null) window.clearTimeout(seekTimer.current);
    seekTimer.current = null;
    setLoading(false);
    void captureVisibleRef
      .current()
      .catch(() => {})
      .finally(() => flushNavigationRef.current());
  }, []);
  const resumeReads = useCallback(() => {
    if (!suspended.current) return;
    suspended.current = false;
    const current = viewport.current?.snapshot();
    if (!current || !latest.current.visible || latest.current.historyRevision === null) return;
    const scrollport = scroll.current;
    const fraction = current.followsLatest
      ? 1
      : scrollport
        ? viewport.current!.positionFractionAtPixel(
            scrollport.scrollTop + scrollport.clientHeight / 2 - topPadding,
          )
        : 0;
    void loadWindowRef.current(fraction);
  }, []);
  const store = useTranscriptNavigation(
    sessionId,
    navigation,
    publishNavigation,
    suspendReads,
    resumeReads,
  );
  flushNavigationRef.current = store.flush;
  const updateSelection = useCallback(() => {
    const node = scroll.current;
    if (!node) return;
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
          if (row.rank >= minimum && row.rank <= maximum) next.set(key, row);
      }
    }
    const active =
      node.ownerDocument.activeElement?.closest<HTMLElement>("[data-transcript-entry]");
    if (active && node.contains(active)) {
      const key = active.dataset.entryKey;
      const row = key ? rowRecords.current.get(key) : undefined;
      if (key && row) next.set(key, row);
    }
    retainedRows.current = next;
    setRetainedKeys(new Set(next.keys()));
  }, []);

  const queueScrollAdjustment = useCallback((adjustment: number) => {
    if (adjustment !== 0) scrollAdjustment.current = (scrollAdjustment.current ?? 0) + adjustment;
  }, []);

  const saveCurrentState = useCallback(() => {
    const model = viewport.current;
    const node = scroll.current;
    if (!model || !node) return;
    const snapshot = model.snapshot();
    const state: TranscriptNavigationState = {
      historyRevision: snapshot.historyRevision,
      positionVersion: snapshot.positionVersion,
      followsLatest: snapshot.followsLatest,
      anchor:
        snapshot.anchor === null
          ? null
          : {
              entry: snapshot.anchor.entry,
              sourceCursor: snapshot.anchor.sourceCursor,
              viewportOffset: snapshot.anchor.viewportOffset,
            },
      width: node.clientWidth || null,
      profileVersion: transcriptNavigationProfileVersion,
    };
    store.save(state);
  }, [store]);

  const maintainMeasurements = useCallback(
    (next: TranscriptPositionWindow) => {
      const model = viewport.current;
      if (!model || next.state.entryCount === 0 || model.snapshot().measurementCount === 0) return;
      const keep = new Set(next.entries.map(({ position }) => position.rank));
      for (const row of retainedRows.current.values()) keep.add(row.rank);
      const sorted = [...keep].sort((left, right) => left - right);
      let from = 0;
      let adjustment = 0;
      for (const rank of sorted) {
        if (rank > from) adjustment += model.evictMeasurements(from, rank).scrollAdjustment;
        from = rank + 1;
      }
      if (from < next.state.entryCount)
        adjustment += model.evictMeasurements(from, next.state.entryCount).scrollAdjustment;
      queueScrollAdjustment(adjustment);
    },
    [queueScrollAdjustment],
  );

  const setWindow = useCallback(
    (next: TranscriptPositionWindow, seekFraction: number, initial: boolean) => {
      const geometry = transcriptViewportGeometry(sessionId, next, layoutProfile);
      let model = viewport.current;
      let adjustment = 0;
      let resetAdjustment = false;
      if (!model) {
        model = new TranscriptViewportModel(geometry);
        viewport.current = model;
        const width = scroll.current?.clientWidth;
        if (width && width > 0) model.setWidth(width);
        if (initial) {
          const loaded = store.load(width ?? 1);
          const saved = loaded.value.state;
          const pixelReuse = loaded.value.pixelReuse;
          if (
            saved &&
            saved.historyRevision === geometry.historyRevision &&
            !saved.followsLatest &&
            saved.anchor &&
            next.target &&
            sameEntry(saved.anchor.entry, next.target)
          ) {
            const anchor: TranscriptViewportAnchor = {
              entry: next.target,
              rank: next.target.rank,
              sourceCursor: saved.anchor.sourceCursor,
              viewportOffset: pixelReuse ? saved.anchor.viewportOffset : 0,
            };
            if (model.jumpToAnchor(anchor)) {
              pendingRestoreRef.current = saved.anchor.sourceCursor
                ? { ...saved.anchor, viewportOffset: pixelReuse ? saved.anchor.viewportOffset : 0 }
                : null;
              setPendingRestore(pendingRestoreRef.current);
            }
          } else model.jumpToLatest();
        }
      } else {
        const oldSnapshot = model.snapshot();
        if (
          geometry.historyRevision < oldSnapshot.historyRevision ||
          (geometry.historyRevision === oldSnapshot.historyRevision &&
            geometry.positionVersion < oldSnapshot.positionVersion)
        ) {
          setLoading(false);
          return;
        }
        const revisionChanged = geometry.historyRevision !== oldSnapshot.historyRevision;
        if (revisionChanged && pendingWidthRestoreRef.current !== null) {
          pendingWidthRestoreRef.current = null;
          pendingRestoreRef.current = null;
          setPendingRestore(null);
        }
        let fallback: TranscriptViewportAnchor | null = null;
        resetAdjustment = revisionChanged;
        if (revisionChanged && !oldSnapshot.followsLatest) {
          const target = next.target ?? next.entries[0]?.position;
          const rootTop = scroll.current?.scrollTop ?? 0;
          if (target)
            fallback = {
              entry: target,
              rank: target.rank,
              sourceCursor: null,
              viewportOffset:
                topPadding + target.prefixWeight * layoutProfile.pixelsPerWeight - rootTop,
            };
        }
        const update = revisionChanged
          ? model.updateGeometry(geometry, fallback)
          : geometry.positionVersion > oldSnapshot.positionVersion
            ? model.updateGeometry(geometry)
            : model.refreshGeometryWindow(geometry);
        adjustment = update.scrollAdjustment;
        if (
          revisionChanged &&
          pendingRestoreRef.current?.entry.historyRevision !== geometry.historyRevision
        ) {
          pendingRestoreRef.current = null;
          setPendingRestore(null);
        }
        if (seekFraction >= 0.999 && oldSnapshot.followsLatest) {
          model.jumpToLatest();
          resetAdjustment = true;
        } else if (next.target && !revisionChanged) {
          const anchor = oldSnapshot.anchor;
          const anchorIncluded =
            anchor !== null &&
            next.entries.some(({ position }) => sameEntry(position, anchor.entry));
          const targetOutsideAnchor =
            !anchorIncluded && (anchor === null || Math.abs(anchor.rank - next.target.rank) > 20);
          if (targetOutsideAnchor) {
            if (pendingWidthRestoreRef.current !== null) {
              pendingWidthRestoreRef.current = null;
              pendingRestoreRef.current = null;
              setPendingRestore(null);
            }
            const root = scroll.current;
            const offset = root
              ? topPadding + model.estimatedPrefixHeight(next.target.rank) - root.scrollTop
              : 0;
            model.recordAnchor({
              entry: next.target,
              rank: next.target.rank,
              sourceCursor: null,
              viewportOffset: offset,
            });
            adjustment = 0;
            resetAdjustment = true;
          }
        }
      }
      updateSelection();
      positionWindowRef.current = next;
      setPositionWindow(next);
      if (resetAdjustment) scrollAdjustment.current = null;
      queueScrollAdjustment(adjustment);
      setModelVersion((value) => value + 1);
      setLoading(false);
      setError(null);
      maintainMeasurements(next);
      saveCurrentState();
    },
    [
      maintainMeasurements,
      queueScrollAdjustment,
      saveCurrentState,
      sessionId,
      store,
      updateSelection,
    ],
  );

  const loadWindow = useCallback(
    async (fraction: number) => {
      const revision = latest.current.historyRevision;
      if (suspended.current || !latest.current.visible || revision === null) return;

      activeRequest.current?.abort();
      const controller = new AbortController();
      activeRequest.current = controller;
      const generation = ++requestGeneration.current;
      desiredFraction.current = fraction;
      setLoading(true);
      try {
        const next = await client.locatePosition(sessionId, revision, fraction, controller.signal);
        if (
          controller.signal.aborted ||
          suspended.current ||
          generation !== requestGeneration.current ||
          latest.current.historyRevision !== revision
        )
          return;
        setWindow(next, fraction, !initialized.current);
        initialized.current = true;
      } catch (cause) {
        if (!controller.signal.aborted && generation === requestGeneration.current) {
          setError(cause instanceof Error ? cause.message : "Transcript position read failed.");
          setLoading(false);
        }
      } finally {
        if (activeRequest.current === controller) activeRequest.current = null;
      }
    },
    [client, sessionId, setWindow],
  );
  loadWindowRef.current = loadWindow;

  useEffect(() => {
    const node = scroll.current;
    if (!node) return;
    const observer = new ResizeObserver(() => {
      setScrollVersion((value) => value + 1);
      const width = node.clientWidth;
      const model = viewport.current;
      if (width <= 0 || !model || model.snapshot().width === width) return;
      const before = model.snapshot();
      const anchor = before.followsLatest ? null : before.anchor;
      const key = anchor ? entryKey(anchor.entry) : null;
      const row = key ? rowRecords.current.get(key) : undefined;
      const saved = anchor && row ? savedSourcePosition(anchor, row) : null;
      widthGeneration.current++;
      const update = model.setWidth(width);
      const hadPendingRestore = pendingWidthRestoreRef.current !== null;
      pendingWidthRestoreRef.current = saved && key ? { key, width, saved } : null;
      if (pendingWidthRestoreRef.current && anchor) {
        pendingRestoreRef.current = anchor;
        setPendingRestore(anchor);
      } else if (hadPendingRestore) {
        pendingRestoreRef.current = null;
        setPendingRestore(null);
      }
      scrollAdjustment.current = update.scrollAdjustment === 0 ? null : update.scrollAdjustment;
      setModelVersion((value) => value + 1);
      saveCurrentState();
    });
    observer.observe(node);
    return () => observer.disconnect();
  }, [root, saveCurrentState]);

  useEffect(() => {
    if (initialized.current || !root || !visible || historyRevision === null) return;
    const width = root.clientWidth;
    if (width <= 0) return;
    const stored = store.load(width).value.state;
    const restore =
      stored && stored.historyRevision === historyRevision && !stored.followsLatest && stored.anchor
        ? stored.anchor
        : null;
    const controller = new AbortController();
    const work = restore
      ? client.getEntryPosition(sessionId, restore.entry, controller.signal)
      : client.locatePosition(sessionId, historyRevision, 1, controller.signal);
    activeRequest.current?.abort();
    activeRequest.current = controller;
    setLoading(true);
    void work
      .then((next) => {
        if (controller.signal.aborted || latest.current.historyRevision !== historyRevision) return;
        setWindow(next, 1, true);
        initialized.current = true;
      })
      .catch((cause: unknown) => {
        if (!controller.signal.aborted) {
          setError(cause instanceof Error ? cause.message : "Transcript position read failed.");
          setLoading(false);
        }
      })
      .finally(() => {
        if (activeRequest.current === controller) activeRequest.current = null;
      });
    return () => controller.abort();
  }, [client, historyRevision, root, sessionId, setWindow, store, visible]);

  useEffect(() => {
    if (!initialized.current || !visible || historyRevision === null || notice === null) return;
    const model = viewport.current;
    const node = scroll.current;
    if (!model || !node) return;
    const snapshot = model.snapshot();
    const fraction = snapshot.followsLatest
      ? 1
      : model.positionFractionAtPixel(node.scrollTop + node.clientHeight / 2 - topPadding);
    void loadWindow(fraction);
  }, [historyRevision, notice, visible, loadWindow]);

  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node || !initialized.current) return;
    const model = viewport.current;
    if (!model) return;
    if (scrollAdjustment.current !== null) {
      const adjustment = scrollAdjustment.current;
      scrollAdjustment.current = null;
      if (model.snapshot().followsLatest) {
        node.scrollTop = Math.max(0, node.scrollHeight - node.clientHeight);
      } else if (adjustment !== 0) node.scrollTop += adjustment;
      programmaticScroll.current = node.scrollTop;
    }
    const snapshot = model.snapshot();
    if (snapshot.followsLatest) {
      node.scrollTop = Math.max(0, node.scrollHeight - node.clientHeight);
      programmaticScroll.current = node.scrollTop;
    } else if (snapshot.anchor) {
      const top = topPadding + model.estimatedPrefixHeight(snapshot.anchor.rank);
      if (snapshot.anchor.sourceCursor !== null) {
        if (pendingRestoreRef.current !== null) {
          node.scrollTop = Math.max(0, top);
          programmaticScroll.current = node.scrollTop;
        }
      } else {
        node.scrollTop = Math.max(0, top - snapshot.anchor.viewportOffset);
        programmaticScroll.current = node.scrollTop;
      }
    }
    const pending = pendingWidthRestoreRef.current;
    if (pending && node.clientWidth === pending.width) {
      const restore = sourceRestores.current.get(pending.key);
      if (restore) {
        const priorScrollTop = node.scrollTop;
        const restored = restore(pending.saved);
        if (node.scrollTop !== priorScrollTop) programmaticScroll.current = node.scrollTop;
        if (restored && pendingWidthRestoreRef.current === pending) {
          pendingWidthRestoreRef.current = null;
          pendingRestoreRef.current = null;
          setPendingRestore(null);
        }
      }
    }
  }, [modelVersion, positionWindow, root]);

  const onSourceCapture = useCallback(
    (
      key: string,
      capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null,
    ) => {
      if (capture) sourceCaptures.current.set(key, capture);
      else sourceCaptures.current.delete(key);
    },
    [],
  );

  const onSourceRestore = useCallback((key: string, restore: RestoreSource | null) => {
    if (restore) sourceRestores.current.set(key, restore);
    else sourceRestores.current.delete(key);
    if (pendingWidthRestoreRef.current?.key === key) setModelVersion((value) => value + 1);
  }, []);

  const scheduleSourceCapture = useCallback(() => {
    if (captureFrame.current !== null) return;
    captureFrame.current = requestAnimationFrame(() => {
      captureFrame.current = null;
      void captureVisibleRef.current().catch(() => {});
    });
  }, []);

  const onPublished = useCallback(
    (key: string) => {
      if (pendingWidthRestoreRef.current?.key === key) {
        setModelVersion((value) => value + 1);
        return;
      }
      const restore = pendingRestoreRef.current;
      const currentWindow = positionWindowRef.current;
      if (restore && currentWindow?.target && entryKey(currentWindow.target) === key) {
        pendingRestoreRef.current = null;
        setPendingRestore(null);
      }
      scheduleSourceCapture();
    },
    [scheduleSourceCapture],
  );

  const onMeasure = useCallback(
    (descriptor: TranscriptEntryDescriptor, rank: number, height: number) => {
      const model = viewport.current;
      const row = positionWindowRef.current?.entries.find(({ position }) => position.rank === rank);
      if (!model || !row || !Number.isFinite(height) || height <= 0) return;
      const measurement: TranscriptMeasurement = {
        entry: descriptor.entry,
        rank,
        afterSourceCursor: null,
        estimatedHeight: row.position.weight * layoutProfile.pixelsPerWeight,
        measuredHeight: height,
        generation: model.snapshot().generation,
      };
      const result = model.recordMeasurement(measurement);
      if (!result.accepted) return;
      queueScrollAdjustment(result.scrollAdjustment);
      setModelVersion((value) => value + 1);
      scheduleSourceCapture();
    },
    [queueScrollAdjustment, scheduleSourceCapture],
  );

  const allRows = new Map<string, PositionedRow>();
  for (const row of positionWindow ? positionedRows(positionWindow) : [])
    allRows.set(entryKey(row.descriptor.entry), row);
  for (const [key, row] of retainedRows.current) if (!allRows.has(key)) allRows.set(key, row);
  rowRecords.current = allRows;

  const visibleEntries = (() => {
    const model = viewport.current;
    const node = scroll.current;
    if (!model || !node || !positionWindow) return [];
    const snapshot = model.snapshot();
    const maximum = Math.max(
      0,
      snapshot.correctedTotalHeight + topPadding + bottomPadding - node.clientHeight,
    );
    // Height correction precedes DOM scroll clamping. Keep the next viewport mounted,
    // rather than evicting a large row against the browser's obsolete scroll offset.
    const nextTop = snapshot.followsLatest
      ? maximum
      : Math.max(0, Math.min(maximum, node.scrollTop + (scrollAdjustment.current ?? 0)));
    return visibleRows(
      positionedRows(positionWindow),
      model,
      nextTop,
      node.clientHeight,
      retainedKeys,
    );
  })();
  const selectedRows = [...retainedRows.current.values()].filter(
    (row) =>
      !visibleEntries.some(
        (visibleRow) => entryKey(visibleRow.descriptor.entry) === entryKey(row.descriptor.entry),
      ),
  );
  const rows = [...visibleEntries, ...selectedRows].sort((left, right) => left.rank - right.rank);
  const estimatedHeight = viewport.current?.snapshot().correctedTotalHeight ?? 0;
  const scrollRef = useCallback((node: HTMLDivElement | null) => {
    scroll.current = node;
    setRoot(node);
  }, []);

  const captureVisibleAnchor = useCallback(async () => {
    const node = scroll.current;
    const model = viewport.current;
    if (!node || !model) return;
    const initial = model.snapshot();
    if (initial.followsLatest || node.clientWidth !== initial.width) return;
    const captureWidthGeneration = widthGeneration.current;
    const portTop = node.getBoundingClientRect().top;
    const elements = visibleElements(node);
    const first = elements[0];
    if (!first?.dataset.entryKey) return;
    let key = first.dataset.entryKey;
    let row = rowRecords.current.get(key);
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
        const candidate = candidateKey ? rowRecords.current.get(candidateKey) : null;
        const capture = candidateKey ? sourceCaptures.current.get(candidateKey) : null;
        if (!candidate?.descriptor.text || !capture) continue;
        let saved: MarkdownSavedSourcePosition | null = null;
        try {
          saved = await capture(controller.signal);
        } catch {
          // Keep the entry-top fallback if no visible source can be captured.
        }
        if (controller.signal.aborted) return;
        const checked = saved && captureMessagePosition(candidate.descriptor.text, saved);
        if (!checked) continue;
        key = candidateKey!;
        row = candidate;
        sourcePosition = checked;
        sourceCursor = cursorFor(checked);
        offset = checked.offset;
        break;
      }
    } finally {
      if (sourceRequest.current === controller) sourceRequest.current = null;
    }
    const currentRow = rowRecords.current.get(key);
    const currentSnapshot = model.snapshot();
    if (
      viewport.current !== model ||
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
    if (model.recordAnchor(anchor)) {
      if (sourcePosition && captureWidthGeneration !== widthGeneration.current) {
        const width = model.snapshot().width;
        if (width !== null && width > 0 && node.clientWidth === width) {
          pendingWidthRestoreRef.current = { key, width, saved: sourcePosition };
          pendingRestoreRef.current = anchor;
          setPendingRestore(anchor);
          setModelVersion((value) => value + 1);
        }
      }
      scrollAdjustment.current = null;
      saveCurrentState();
    }
  }, [saveCurrentState]);
  captureVisibleRef.current = captureVisibleAnchor;

  function rememberScroll() {
    setScrollVersion((value) => value + 1);
    const node = scroll.current;
    const model = viewport.current;
    if (!node || !model || !positionWindowRef.current) return;
    const priorTarget = programmaticScroll.current;
    const actualUserScroll = priorTarget === null || priorTarget !== node.scrollTop;
    programmaticScroll.current = null;
    if (actualUserScroll) {
      if (pendingWidthRestoreRef.current !== null || pendingRestoreRef.current !== null) {
        pendingWidthRestoreRef.current = null;
        pendingRestoreRef.current = null;
        setPendingRestore(null);
      }
      sourceRequest.current?.abort();
      sourceRequest.current = null;
      const atLatest = node.scrollHeight - node.scrollTop - node.clientHeight < 40;
      const portTop = node.getBoundingClientRect().top;
      const first = firstVisibleRow(node);
      const key = first?.dataset.entryKey;
      const row = key ? rowRecords.current.get(key) : undefined;
      const offset = first ? first.getBoundingClientRect().top - portTop : 0;
      let recorded = false;
      if (atLatest) recorded = model.recordUserScroll(null, true);
      else if (row)
        recorded = model.recordUserScroll(
          {
            entry: row.descriptor.entry,
            rank: row.rank,
            sourceCursor: null,
            viewportOffset: offset,
          },
          false,
        );
      else recorded = model.recordUserScroll(null, false);
      if (recorded) scrollAdjustment.current = null;
      saveCurrentState();
      void captureVisibleAnchor();
      const snapshot = model.snapshot();
      const fraction = snapshot.followsLatest
        ? 1
        : model.positionFractionAtPixel(node.scrollTop + node.clientHeight / 2 - topPadding);
      desiredFraction.current = fraction;
      if (!windowCoversFraction(positionWindowRef.current, fraction)) scheduleSeek(fraction);
    }
  }

  function scheduleSeek(fraction: number) {
    desiredFraction.current = fraction;
    if (seekTimer.current !== null) window.clearTimeout(seekTimer.current);
    seekTimer.current = window.setTimeout(() => {
      seekTimer.current = null;
      void loadWindow(desiredFraction.current);
    }, seekDebounceMs);
  }

  function jumpLatest() {
    const model = viewport.current;
    if (!model) return;
    sourceRequest.current?.abort();
    sourceRequest.current = null;
    model.jumpToLatest();
    pendingWidthRestoreRef.current = null;
    pendingRestoreRef.current = null;
    setPendingRestore(null);
    scrollAdjustment.current = null;
    saveCurrentState();
    const node = scroll.current;
    if (node) {
      node.scrollTop = node.scrollHeight;
      programmaticScroll.current = node.scrollTop;
    }
    void loadWindow(1);
    setModelVersion((value) => value + 1);
  }

  useEffect(() => {
    const selectionChanged = () => updateSelection();
    document.addEventListener("selectionchange", selectionChanged);
    document.addEventListener("focusin", selectionChanged);
    document.addEventListener("focusout", selectionChanged);
    return () => {
      document.removeEventListener("selectionchange", selectionChanged);
      document.removeEventListener("focusin", selectionChanged);
      document.removeEventListener("focusout", selectionChanged);
    };
  }, [updateSelection]);
  useEffect(() => {
    if (!visible || documentInactive) suspendReads();
    else resumeReads();
  }, [documentInactive, resumeReads, suspendReads, visible]);
  useEffect(() => {
    const update = () => setDocumentInactive(document.hidden);
    document.addEventListener("visibilitychange", update);
    return () => document.removeEventListener("visibilitychange", update);
  }, []);
  useEffect(
    () => () => {
      if (seekTimer.current !== null) window.clearTimeout(seekTimer.current);
      if (captureFrame.current !== null) cancelAnimationFrame(captureFrame.current);
      activeRequest.current?.abort();
      sourceRequest.current?.abort();
      store.flush();
    },
    [store],
  );

  const spacerHeight = Math.max(0, estimatedHeight + topPadding + bottomPadding);
  void scrollVersion;
  return (
    <div class="transcript-area">
      <div
        class="transcript transcript-virtual"
        ref={scrollRef}
        onScroll={rememberScroll}
        role="region"
        aria-label="Conversation"
        aria-busy={loading}
        tabIndex={0}
      >
        <div class="messages messages-virtual" style={{ height: `${spacerHeight}px` }}>
          {rows.map((row) => {
            const key = entryKey(row.descriptor.entry);
            const restore =
              pendingRestore?.entry && sameEntry(pendingRestore.entry, row.descriptor.entry)
                ? pendingRestore
                : null;
            const saved =
              restore?.sourceCursor && row.descriptor.text
                ? {
                    cursor: {
                      message: row.descriptor.text.from.message,
                      segmentIndex: restore.sourceCursor.segmentIndex,
                      utf8Offset: restore.sourceCursor.utf8Offset,
                    },
                    offset: restore.viewportOffset,
                  }
                : undefined;
            const top = topPadding + (viewport.current?.estimatedPrefixHeight(row.rank) ?? 0);
            return (
              <VirtualEntry
                key={key}
                display={display}
                streamId={streamId}
                sessionId={sessionId}
                descriptor={row.descriptor}
                rank={row.rank}
                top={top}
                root={root}
                paused={!visible || documentInactive}
                saved={saved}
                onMeasure={onMeasure}
                onSourceCapture={onSourceCapture}
                onSourceRestore={onSourceRestore}
                onPublished={onPublished}
              />
            );
          })}
          {!positionWindow && !error && <p class="history-note">Loading messages…</p>}
          {error && (
            <p class="inline-error">
              {error}{" "}
              <button onClick={() => void loadWindow(desiredFraction.current)}>Retry</button>
            </p>
          )}
        </div>
      </div>
      {viewport.current && !viewport.current.snapshot().followsLatest && (
        <IconButton
          icon="arrow-down"
          label="Jump to latest"
          class="jump-latest"
          onClick={jumpLatest}
        />
      )}
    </div>
  );
}
