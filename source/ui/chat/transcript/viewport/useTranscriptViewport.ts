import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
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
  captureTranscriptDraftAnchor,
  layoutProfile,
  pinTranscriptLatest,
  recordTranscriptUserScroll,
  restoreTranscriptDraftAnchor,
  topPadding,
  transcriptScrollFraction,
  type TranscriptDraftAnchor,
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
  const messages = useRef<HTMLDivElement>(null);
  const tailHeight = useRef(0);
  const [root, setRoot] = useState<HTMLDivElement | null>(null);
  const [positionWindow, setPositionWindow] = useState<TranscriptPositionWindow | null>(null);
  const hasWindow = positionWindow !== null;
  const positionWindowRef = useRef<TranscriptPositionWindow | null>(null);
  const viewport = useRef<TranscriptViewportModel | null>(null);
  const [modelVersion, setModelVersion] = useState(0);
  const [, setScrollVersion] = useState(0);
  const [documentInactive, setDocumentInactive] = useState(() => document.hidden);
  const {
    lookupRow,
    getRetainedRows,
    lastScrollport,
    updateSelection,
    prepareMountedRows,
    coversScrollTop,
  } = useTranscriptRows(scroll, historyRevision, positionWindow);
  const renderedActive = useRef(visible && !documentInactive);
  const resumingVisibility = visible && !documentInactive && !renderedActive.current;
  renderedActive.current = visible && !documentInactive;
  const flushNavigationRef = useRef<() => void>(() => {});
  const scrollAdjustment = useRef<number | null>(null);
  const programmaticScroll = useRef<number | null>(null);
  const draftAnchor = useRef<TranscriptDraftAnchor[] | null>(null);

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
  const keepDraftAnchorInPlace = useCallback(
    (node: HTMLElement) => {
      const anchor = draftAnchor.current;
      if (!anchor) return;
      if (!anchor.some(({ row }) => node.contains(row))) {
        draftAnchor.current = null;
        return;
      }
      const target = restoreTranscriptDraftAnchor(node, anchor);
      if (target !== null) onProgrammaticScroll(target);
    },
    [onProgrammaticScroll],
  );
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
      const model = viewport.current;
      if (!model || !visible || documentInactive || node.clientHeight === 0) return;
      // ResizeObserver runs before paint; a height-only resize does not necessarily
      // resize the content or trigger the model layout effect.
      const target = pinTranscriptLatest(node, model);
      if (target !== null) onProgrammaticScroll(target);
      if (width <= 0 || model.snapshot().width === width) return;
      const adjustment = resizeViewport(model, width);
      scrollAdjustment.current = adjustment === 0 ? null : adjustment;
      setModelVersion((value) => value + 1);
      saveCurrentState();
    });
    observer.observe(node);
    return () => observer.disconnect();
  }, [documentInactive, onProgrammaticScroll, resizeViewport, root, saveCurrentState, visible]);

  const mountedRowsCoverScroll = useCallback(
    (node: HTMLElement) => {
      const model = viewport.current;
      if (!model) return false;
      const snapshot = model.snapshot();
      const maximum = Math.max(
        0,
        snapshot.correctedTotalHeight +
          topPadding +
          bottomPadding +
          tailHeight.current -
          node.clientHeight,
      );
      const top = snapshot.followsLatest
        ? maximum
        : Math.max(0, Math.min(maximum, node.scrollTop + (scrollAdjustment.current ?? 0)));
      return coversScrollTop(node, top);
    },
    [coversScrollTop],
  );

  useLayoutEffect(() => {
    const content = messages.current;
    if (!root || !content || !visible || documentInactive) return;
    const observer = new ResizeObserver(() => {
      if (root.clientHeight === 0) return;
      const prefix = Number.parseFloat(content.style.paddingTop);
      tailHeight.current = Math.max(0, Math.round(content.offsetHeight - prefix - bottomPadding));
      // Drafts are in flow, so their current animated height is already in scrollHeight.
      // Pin in the observer, before paint, rather than waiting for another render.
      const model = viewport.current;
      const target = model ? pinTranscriptLatest(root, model) : null;
      if (target !== null) onProgrammaticScroll(target);
      else keepDraftAnchorInPlace(root);
      if (!mountedRowsCoverScroll(root)) setScrollVersion((value) => value + 1);
    });
    observer.observe(content, { box: "border-box" });
    return () => observer.disconnect();
  }, [
    documentInactive,
    keepDraftAnchorInPlace,
    mountedRowsCoverScroll,
    onProgrammaticScroll,
    root,
    visible,
  ]);

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
    keepDraftAnchorInPlace(node);
  }, [
    applyWidthRestore,
    documentInactive,
    hasPendingRestore,
    isInitialized,
    keepDraftAnchorInPlace,
    lastScrollport,
    modelVersion,
    onProgrammaticScroll,
    positionWindow,
    resumingVisibility,
    root,
    visible,
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
    tailHeight: tailHeight.current,
  });
  const estimatedHeight = viewport.current?.snapshot().correctedTotalHeight ?? 0;
  const scrollRef = useCallback((node: HTMLDivElement | null) => {
    scroll.current = node;
    setRoot(node);
  }, []);

  function rememberScroll() {
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
    if (!actualUserScroll) {
      if (!mountedRowsCoverScroll(node)) setScrollVersion((value) => value + 1);
      return;
    }
    setScrollVersion((value) => value + 1);
    interruptForUserScroll();
    if (recordTranscriptUserScroll(node, model, lookupRow)) scrollAdjustment.current = null;
    const snapshot = model.snapshot();
    draftAnchor.current =
      !snapshot.followsLatest && !snapshot.anchor ? captureTranscriptDraftAnchor(node) : null;
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
    draftAnchor.current = null;
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

  const windowEntries = useMemo(
    () =>
      positionWindow?.state.sessionId === sessionId
        ? positionWindow.entries.map(({ descriptor }) => descriptor)
        : null,
    [positionWindow, sessionId],
  );
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
    tailTop: topPadding + estimatedHeight,
    messagesRef: messages,
    followsLatest: viewport.current?.snapshot().followsLatest ?? false,
    windowRevision: positionWindow?.state.historyRevision ?? null,
    liveWindowStart: positionWindow?.entries[0]?.position.rank ?? 0,
    hasWindow,
    windowEntries,
    liveWindowEntries: positionWindow && !positionWindow.hasAfter ? windowEntries : null,
    loading: reading.loading,
    error: reading.error,
    retry: reading.retry,
    showJumpLatest: viewport.current !== null && !viewport.current.snapshot().followsLatest,
    jumpLatest,
  };
}
