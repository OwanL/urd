import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import type {
  TranscriptEntryDescriptor,
  TranscriptPositionWindow,
} from "../../../sessions/transcript/Messages.ts";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { TranscriptMeasurement } from "./TranscriptViewport.ts";
import { TranscriptViewport as TranscriptViewportModel } from "./TranscriptViewport.ts";
import { transcriptViewportGeometry } from "./TranscriptGeometry.ts";
import type { TranscriptNavigationState } from "../navigation/TranscriptNavigationStorage.ts";
import { useTranscriptNavigation } from "../navigation/useTranscriptNavigation.ts";
import { usePositionWindow } from "../reading/usePositionWindow.ts";
import { entryKey, windowCoversFraction } from "./TranscriptRows.ts";
import {
  alignTranscriptScrollport,
  bottomPadding,
  layoutProfile,
  recordTranscriptUserScroll,
  topPadding,
  transcriptScrollFraction,
} from "./TranscriptScrollLayout.ts";
import {
  applyTranscriptWindow,
  evictMeasurementsOutsideWindow,
} from "./TranscriptWindowApplication.ts";
import {
  readViewportNavigation,
  restoreInitialWindowAnchor,
} from "./TranscriptViewportNavigation.ts";
import { useTranscriptRows, useTranscriptSelectionEvents } from "./useTranscriptRows.ts";
import { useTranscriptSourceAnchors } from "./useTranscriptSourceAnchors.ts";

interface TranscriptViewportOptions {
  client: SessionClient;
  sessionId: string;
  historyRevision: number | null;
  notice: string | null;
  visible: boolean;
  navigation: TranscriptNavigationState | undefined;
  onNavigationChange: (state: TranscriptNavigationState) => void;
}

// Coordinates row geometry and source anchors with the mounted scrollport.
export function useTranscriptViewport({
  client,
  sessionId,
  historyRevision,
  notice,
  visible,
  navigation,
  onNavigationChange,
}: TranscriptViewportOptions) {
  const scroll = useRef<HTMLDivElement>(null);
  const tail = useRef<HTMLDivElement>(null);
  const [tailHeight, setTailHeight] = useState(0);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [positionWindow, setPositionWindow] = useState<TranscriptPositionWindow | null>(null);
  const hasWindow = positionWindow !== null;
  const positionWindowRef = useRef<TranscriptPositionWindow | null>(null);
  const viewport = useRef<TranscriptViewportModel | null>(null);
  const [modelVersion, setModelVersion] = useState(0);
  const [, setScrollVersion] = useState(0);
  const [documentInactive, setDocumentInactive] = useState(() => document.hidden);
  const { lookupRow, getRetainedRows, lastScrollport, updateSelection, prepareMountedRows } =
    useTranscriptRows(scroll, historyRevision);
  const renderedActive = useRef(visible && !documentInactive);
  const resumingVisibility = visible && !documentInactive && !renderedActive.current;
  renderedActive.current = visible && !documentInactive;
  const flushNavigationRef = useRef<() => void>(() => {});
  const scrollAdjustment = useRef<number | null>(null);
  const programmaticScroll = useRef<number | null>(null);

  // Scrolling already recorded the reading position, so suspension only persists it.
  const onReadsSuspended = useCallback(() => flushNavigationRef.current(), []);
  // Install page lifecycle listeners before the effects that start position reads.
  const readLifecycle = useRef<Pick<
    ReturnType<typeof usePositionWindow>,
    "suspend" | "resume" | "isSuspended"
  > | null>(null);
  const store = useTranscriptNavigation(
    sessionId,
    navigation,
    onNavigationChange,
    () => readLifecycle.current?.suspend(),
    () => readLifecycle.current?.resume(),
  );
  flushNavigationRef.current = store.flush;

  const queueScrollAdjustment = useCallback((adjustment: number) => {
    if (adjustment !== 0) scrollAdjustment.current = (scrollAdjustment.current ?? 0) + adjustment;
  }, []);

  const saveCurrentState = useCallback(() => {
    const model = viewport.current;
    const node = scroll.current;
    if (model && node) store.save(readViewportNavigation(model, node.clientWidth));
  }, [store]);
  const isSuspended = useCallback(() => readLifecycle.current?.isSuspended() ?? false, []);
  const requestLayout = useCallback(() => setModelVersion((value) => value + 1), []);
  const onProgrammaticScroll = useCallback((top: number) => {
    programmaticScroll.current = top;
  }, []);
  const onAnchorRecorded = useCallback(() => {
    scrollAdjustment.current = null;
    saveCurrentState();
  }, [saveCurrentState]);
  const {
    windowRestore,
    clearRestore,
    hasPendingRestore,
    resizeViewport,
    applyWidthRestore,
    rowSources,
    abortCapture,
    interruptForUserScroll,
    captureVisibleAnchor,
    scheduleSourceCapture,
    cancelCaptureFrame,
    savedPositionForRow,
  } = useTranscriptSourceAnchors({
    scroll,
    viewport,
    resumingVisibility,
    lookupRow,
    isSuspended,
    requestLayout,
    onProgrammaticScroll,
    onAnchorRecorded,
  });

  const setWindow = useCallback(
    (
      next: TranscriptPositionWindow,
      seekFraction: number,
      initial: boolean,
      finishRead: (accepted: boolean) => void,
    ) => {
      const geometry = transcriptViewportGeometry(sessionId, next, layoutProfile);
      let model = viewport.current;
      let adjustment = 0;
      let resetAdjustment = false;
      if (!model) {
        model = new TranscriptViewportModel(geometry);
        viewport.current = model;
        const width = scroll.current?.clientWidth;
        if (width && width > 0) model.setWidth(width);
        if (initial)
          restoreInitialWindowAnchor(
            model,
            next,
            width ?? 1,
            store.load,
            windowRestore.restoreAnchor,
          );
      } else {
        const applied = applyTranscriptWindow(
          model,
          geometry,
          next,
          seekFraction,
          scroll.current,
          windowRestore,
        );
        if (!applied) {
          finishRead(false);
          return;
        }
        ({ adjustment, resetAdjustment } = applied);
      }
      updateSelection();
      positionWindowRef.current = next;
      setPositionWindow(next);
      if (resetAdjustment) scrollAdjustment.current = null;
      queueScrollAdjustment(adjustment);
      setModelVersion((value) => value + 1);
      finishRead(true);
      queueScrollAdjustment(evictMeasurementsOutsideWindow(model, next, getRetainedRows()));
      saveCurrentState();
    },
    [
      getRetainedRows,
      queueScrollAdjustment,
      saveCurrentState,
      sessionId,
      store,
      updateSelection,
      windowRestore,
    ],
  );

  const getReadFraction = useCallback((fallbackWithoutScrollport?: number) => {
    const model = viewport.current;
    const node = scroll.current;
    if (!model || (!node && fallbackWithoutScrollport === undefined)) return null;
    if (node) return transcriptScrollFraction(node, model);
    return model.snapshot().followsLatest ? 1 : (fallbackWithoutScrollport ?? null);
  }, []);

  useEffect(() => {
    const node = scroll.current;
    if (!node) return;
    const observer = new ResizeObserver(() => {
      setScrollVersion((value) => value + 1);
      const width = node.clientWidth;
      if (width > 0) setTailHeight(tail.current?.offsetHeight ?? 0);
      const model = viewport.current;
      if (width <= 0 || !model || model.snapshot().width === width) return;
      const adjustment = resizeViewport(model, width);
      scrollAdjustment.current = adjustment === 0 ? null : adjustment;
      setModelVersion((value) => value + 1);
      saveCurrentState();
    });
    observer.observe(node);
    if (tail.current) observer.observe(tail.current);
    return () => observer.disconnect();
  }, [resizeViewport, root, saveCurrentState]);

  const reading = usePositionWindow({
    client,
    sessionId,
    historyRevision,
    notice,
    visible,
    documentInactive,
    root,
    hasWindow,
    store,
    getReadFraction,
    setWindow,
    onSuspended: onReadsSuspended,
  });
  readLifecycle.current = reading;
  const { isInitialized, suspend, resume, clearSeekTimer, cancelRead } = reading;

  useLayoutEffect(() => {
    const node = scroll.current;
    if (!node || !isInitialized() || !visible || documentInactive || node.clientHeight === 0)
      return;
    const model = viewport.current;
    if (!model) return;
    const adjustment = scrollAdjustment.current;
    scrollAdjustment.current = null;
    const target = alignTranscriptScrollport(
      node,
      model,
      adjustment,
      resumingVisibility ? lastScrollport() : null,
      hasPendingRestore,
    );
    if (target !== null) programmaticScroll.current = target;
    applyWidthRestore(node, onProgrammaticScroll);
  }, [
    applyWidthRestore,
    documentInactive,
    hasPendingRestore,
    isInitialized,
    lastScrollport,
    modelVersion,
    onProgrammaticScroll,
    positionWindow,
    resumingVisibility,
    root,
    visible,
    tailHeight,
  ]);

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

  const positioned = prepareMountedRows({
    window: positionWindow,
    model: viewport.current,
    scrollport: scroll.current,
    suspended: !visible || documentInactive || resumingVisibility,
    scrollAdjustment: scrollAdjustment.current ?? 0,
    tailHeight,
  });
  const estimatedHeight = viewport.current?.snapshot().correctedTotalHeight ?? 0;
  const scrollRef = useCallback((node: HTMLDivElement | null) => {
    scroll.current = node;
    setRoot(node);
  }, []);

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
    if (!actualUserScroll) return;
    interruptForUserScroll();
    if (recordTranscriptUserScroll(node, model, lookupRow)) scrollAdjustment.current = null;
    saveCurrentState();
    void captureVisibleAnchor();
    const fraction = transcriptScrollFraction(node, model);
    reading.updateSeek(fraction, windowCoversFraction(positionWindowRef.current, fraction));
  }

  function jumpLatest() {
    const model = viewport.current;
    if (!model) return;
    abortCapture();
    model.jumpToLatest();
    clearRestore();
    scrollAdjustment.current = null;
    saveCurrentState();
    const node = scroll.current;
    if (node) {
      node.scrollTop = node.scrollHeight;
      programmaticScroll.current = node.scrollTop;
    }
    void reading.loadWindow(1);
    setModelVersion((value) => value + 1);
  }

  useTranscriptSelectionEvents(updateSelection);
  useEffect(() => {
    if (!visible || documentInactive) suspend();
    else resume();
  }, [documentInactive, resume, suspend, visible]);
  useEffect(() => {
    const update = () => setDocumentInactive(document.hidden);
    document.addEventListener("visibilitychange", update);
    return () => document.removeEventListener("visibilitychange", update);
  }, []);
  useEffect(
    () => () => {
      clearSeekTimer();
      cancelCaptureFrame();
      cancelRead();
      abortCapture();
      store.flush();
    },
    [abortCapture, cancelCaptureFrame, cancelRead, clearSeekTimer, store],
  );

  const spacerHeight = Math.max(0, estimatedHeight + topPadding + bottomPadding + tailHeight);
  const rows = positioned.map((row) => {
    const key = entryKey(row.descriptor.entry);
    const saved = savedPositionForRow(row);
    const top = topPadding + (viewport.current?.estimatedPrefixHeight(row.rank) ?? 0);
    return { ...row, key, top, saved };
  });
  return {
    root,
    scrollRef,
    rememberScroll,
    rows,
    paused: !visible || documentInactive,
    onMeasure,
    ...rowSources,
    spacerHeight,
    tailTop: topPadding + estimatedHeight,
    tailRef: tail,
    hasWindow,
    loading: reading.loading,
    error: reading.error,
    retry: reading.retry,
    showJumpLatest: viewport.current !== null && !viewport.current.snapshot().followsLatest,
    jumpLatest,
  };
}
