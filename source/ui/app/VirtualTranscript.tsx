import type { ComponentChildren } from "preact";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { MarkdownSavedSourcePosition } from "../markdown-rendering/MarkdownMessageView.tsx";
import type {
  TranscriptEntryDescriptor,
  TranscriptEntryRef,
  TranscriptPositionWindow,
  TranscriptTextFacts,
  ToolCallLabel,
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
import { EditableMessage } from "./EditableMessage.tsx";
import { CompactionMarker } from "./CompactionMarker.tsx";
import { ToolRow } from "../chat/ToolRow.tsx";
import { ToolRowInteractionsProvider } from "../chat/ToolRowInteractions.tsx";
import { toolCallLabel } from "./ToolRowPresentation.ts";
import { TranscriptPositionReader } from "./TranscriptPositionReader.ts";
import "../chat/transcript.css";

const layoutProfile = { version: 1 as const, pixelsPerWeight: 0.1 };
const topPadding = 16;
const bottomPadding = 28;
const seekDebounceMs = 75;

interface VirtualTranscriptProps {
  children?: ComponentChildren;
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
  readonly toolLabel: ToolCallLabel | null;
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
    toolLabel: descriptor.tool ? toolCallLabel(descriptor.tool, window.toolLabels) : null,
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

function VirtualEntry({
  client,
  display,
  streamId,
  sessionId,
  descriptor,
  toolLabel,
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
  client: SessionClient;
  display: TranscriptDisplay;
  streamId: string | null;
  sessionId: string;
  descriptor: TranscriptEntryDescriptor;
  toolLabel: ToolCallLabel | null;
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
      class={`transcript-entry${descriptor.tool ? " transcript-tool-entry" : ""}`}
      data-transcript-entry=""
      data-entry-key={key}
      data-entry-rank={rank}
      style={{ top: `${top}px` }}
    >
      {isTextEntry(descriptor.entry) && descriptor.text ? (
        <EditableMessage entry={descriptor.entry} text={descriptor.text}>
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
        </EditableMessage>
      ) : descriptor.compaction ? (
        <CompactionMarker
          client={client}
          sessionId={sessionId}
          entry={descriptor.entry}
          compaction={descriptor.compaction}
        />
      ) : descriptor.tool ? (
        <ToolRow
          tool={descriptor.tool}
          label={toolLabel}
          completion={descriptor.entry.kind === "toolCompletion"}
          root={root}
          paused={paused}
        />
      ) : (
        <div class={`message message-${role}`} />
      )}
    </div>
  );
}

export function VirtualTranscript({
  children,
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
  const tail = useRef<HTMLDivElement>(null);
  const [tailHeight, setTailHeight] = useState(0);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [positionWindow, setPositionWindow] = useState<TranscriptPositionWindow | null>(null);
  const positionReader = useMemo(
    () => new TranscriptPositionReader(client, sessionId),
    [client, sessionId],
  );
  const positionWindowRef = useRef<TranscriptPositionWindow | null>(null);
  const viewport = useRef<TranscriptViewportModel | null>(null);
  const [modelVersion, setModelVersion] = useState(0);
  const [scrollVersion, setScrollVersion] = useState(0);
  const [readEpoch, setReadEpoch] = useState(0);
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
  const retainedViewportRows = useRef<PositionedRow[]>([]);
  const retainedScrollport = useRef({ top: 0, width: 0 });
  const renderedActive = useRef(visible && !documentInactive);
  const resumingVisibility = visible && !documentInactive && !renderedActive.current;
  renderedActive.current = visible && !documentInactive;
  const [retainedKeys, setRetainedKeys] = useState<ReadonlySet<string>>(new Set());
  const sourceRequest = useRef<AbortController | null>(null);
  const loadWindowRef = useRef<(fraction: number) => Promise<void>>(async () => {});
  const captureVisibleRef = useRef<() => Promise<void>>(async () => {});
  const captureFrame = useRef<number | null>(null);
  const flushNavigationRef = useRef<() => void>(() => {});
  const widthGeneration = useRef(0);
  const suspended = useRef(false);
  const initialized = useRef(false);
  const scrollAdjustment = useRef<number | null>(null);
  const programmaticScroll = useRef<number | null>(null);
  const seekTimer = useRef<number | null>(null);
  const desiredFraction = useRef(1);
  const latest = useRef({ historyRevision, visible, notice });
  latest.current = { historyRevision, visible, notice };
  if (resumingVisibility && pendingRestoreRef.current === null) {
    const snapshot = viewport.current?.snapshot();
    if (snapshot && !snapshot.followsLatest && snapshot.anchor?.sourceCursor)
      pendingRestoreRef.current = snapshot.anchor;
  }

  const publishNavigation = useCallback(
    (state: TranscriptNavigationState) => onNavigationChange(state),
    [onNavigationChange],
  );
  const suspendReads = useCallback(() => {
    if (suspended.current) return;
    suspended.current = true;
    positionReader.cancel();
    if (seekTimer.current !== null) window.clearTimeout(seekTimer.current);
    seekTimer.current = null;
    setLoading(false);
    void captureVisibleRef
      .current()
      .catch(() => {})
      .finally(() => flushNavigationRef.current());
  }, [positionReader]);
  const resumeReads = useCallback(() => {
    if (!suspended.current) return;
    suspended.current = false;
    if (!latest.current.visible || latest.current.historyRevision === null) return;
    const current = viewport.current?.snapshot();
    if (!current) {
      setReadEpoch((epoch) => epoch + 1);
      return;
    }
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
          if (
            row.descriptor.entry.historyRevision === latest.current.historyRevision &&
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
      if (key && row?.descriptor.entry.historyRevision === latest.current.historyRevision)
        next.set(key, row);
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

      desiredFraction.current = fraction;
      setLoading(true);
      const read = positionReader.read({ kind: "fraction", historyRevision: revision, fraction });
      try {
        const next = await read;
        if (
          next === null ||
          !positionReader.isCurrent(read) ||
          suspended.current ||
          !latest.current.visible ||
          latest.current.historyRevision !== revision
        )
          return;
        setWindow(next, fraction, !initialized.current);
        initialized.current = true;
      } catch (cause) {
        if (
          positionReader.isCurrent(read) &&
          !suspended.current &&
          latest.current.visible &&
          latest.current.historyRevision === revision
        ) {
          setError(cause instanceof Error ? cause.message : "Transcript position read failed.");
          setLoading(false);
        }
      }
    },
    [positionReader, setWindow],
  );
  loadWindowRef.current = loadWindow;

  useEffect(() => {
    const node = scroll.current;
    if (!node) return;
    const observer = new ResizeObserver(() => {
      setScrollVersion((value) => value + 1);
      const width = node.clientWidth;
      if (width > 0) setTailHeight(tail.current?.offsetHeight ?? 0);
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
    if (tail.current) observer.observe(tail.current);
    return () => observer.disconnect();
  }, [root, saveCurrentState]);

  useEffect(() => {
    if (
      initialized.current ||
      suspended.current ||
      documentInactive ||
      !root ||
      !visible ||
      historyRevision === null
    )
      return;
    const width = root.clientWidth;
    if (width <= 0) return;
    const stored = store.load(width).value.state;
    const restore =
      stored && stored.historyRevision === historyRevision && !stored.followsLatest && stored.anchor
        ? stored.anchor
        : null;
    const target = restore
      ? { kind: "entry" as const, entry: restore.entry }
      : { kind: "fraction" as const, historyRevision, fraction: 1 };
    setLoading(true);
    const read = positionReader.read(target);
    void read
      .then((next) => {
        if (
          next === null ||
          !positionReader.isCurrent(read) ||
          suspended.current ||
          !latest.current.visible ||
          latest.current.historyRevision !== historyRevision
        )
          return;
        setWindow(next, 1, true);
        initialized.current = true;
      })
      .catch((cause: unknown) => {
        if (
          positionReader.isCurrent(read) &&
          !suspended.current &&
          latest.current.visible &&
          latest.current.historyRevision === historyRevision
        ) {
          setError(cause instanceof Error ? cause.message : "Transcript position read failed.");
          setLoading(false);
        }
      });
    return () => positionReader.cancel();
  }, [
    documentInactive,
    historyRevision,
    readEpoch,
    positionReader,
    root,
    sessionId,
    setWindow,
    store,
    visible,
  ]);

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
    if (!node || !initialized.current || !visible || documentInactive || node.clientHeight === 0)
      return;
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
    if (
      resumingVisibility &&
      !snapshot.followsLatest &&
      retainedScrollport.current.width === node.clientWidth
    ) {
      node.scrollTop = retainedScrollport.current.top;
      programmaticScroll.current = node.scrollTop;
    }
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
  }, [
    documentInactive,
    modelVersion,
    positionWindow,
    resumingVisibility,
    root,
    visible,
    tailHeight,
  ]);

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
      if (restore && entryKey(restore.entry) === key) {
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
    // A hidden retained panel has no layout box. Zero geometry is not a viewport
    // change: preserve its bounded mounted window until it is shown or evicted.
    if (!visible || documentInactive || resumingVisibility || node.clientHeight === 0)
      return retainedViewportRows.current;
    retainedScrollport.current = { top: node.scrollTop, width: node.clientWidth };
    const snapshot = model.snapshot();
    const maximum = Math.max(
      0,
      snapshot.correctedTotalHeight + topPadding + bottomPadding + tailHeight - node.clientHeight,
    );
    // Height correction precedes DOM scroll clamping. Keep the next viewport mounted,
    // rather than evicting a large row against the browser's obsolete scroll offset.
    const nextTop = snapshot.followsLatest
      ? maximum
      : Math.max(0, Math.min(maximum, node.scrollTop + (scrollAdjustment.current ?? 0)));
    return (retainedViewportRows.current = visibleRows(
      positionedRows(positionWindow),
      model,
      nextTop,
      node.clientHeight,
      retainedKeys,
    ));
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
    if (
      !node ||
      !model ||
      suspended.current ||
      pendingRestoreRef.current !== null ||
      node.clientHeight === 0
    )
      return;
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
      suspended.current ||
      pendingRestoreRef.current !== null ||
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
    if (
      !node ||
      !model ||
      !positionWindowRef.current ||
      !visible ||
      documentInactive ||
      node.clientHeight === 0
    )
      return;
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
      positionReader.cancel();
      sourceRequest.current?.abort();
      store.flush();
    },
    [positionReader, store],
  );

  const spacerHeight = Math.max(0, estimatedHeight + topPadding + bottomPadding + tailHeight);
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
          <ToolRowInteractionsProvider
            client={client}
            sessionId={sessionId}
            historyRevision={historyRevision}
            root={root}
          >
            {rows.map((row) => {
              const key = entryKey(row.descriptor.entry);
              const wanted = pendingRestoreRef.current ?? pendingRestore;
              const restore =
                wanted?.entry && sameEntry(wanted.entry, row.descriptor.entry) ? wanted : null;
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
                  client={client}
                  display={display}
                  streamId={streamId}
                  sessionId={sessionId}
                  descriptor={row.descriptor}
                  toolLabel={row.toolLabel}
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
          </ToolRowInteractionsProvider>
          <div
            ref={tail}
            class="transcript-tail"
            style={{ top: `${topPadding + estimatedHeight}px` }}
          >
            {children}
          </div>
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
