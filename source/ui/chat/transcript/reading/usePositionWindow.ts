import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { TranscriptPositionWindow } from "../../../sessions/transcript/Messages.ts";
import type { TranscriptNavigationStore } from "../navigation/useTranscriptNavigation.ts";
import {
  TranscriptPositionReader,
  type TranscriptPositionTarget,
} from "./TranscriptPositionReader.ts";

const seekDebounceMs = 75;

interface PositionWindowOptions {
  client: SessionClient;
  sessionId: string;
  historyRevision: number | null;
  notice: string | null;
  visible: boolean;
  documentInactive: boolean;
  root: HTMLDivElement | null;
  hasWindow: boolean;
  store: TranscriptNavigationStore;
  getReadFraction: (fallbackWithoutScrollport?: number) => number | null;
  setWindow: (
    next: TranscriptPositionWindow,
    seekFraction: number,
    initial: boolean,
    finishRead: (accepted: boolean) => void,
  ) => void;
  onSuspended: () => void;
}

// Owns reads and their lifecycle; the viewport applies each accepted window to its model.
export function usePositionWindow({
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
  onSuspended,
}: PositionWindowOptions) {
  const positionReader = useMemo(
    () => new TranscriptPositionReader(client, sessionId),
    [client, sessionId],
  );
  const [readEpoch, setReadEpoch] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const loadWindowRef = useRef<(fraction: number) => Promise<void>>(async () => {});
  const suspended = useRef(false);
  const initialized = useRef(false);
  const seekTimer = useRef<number | null>(null);
  const desiredFraction = useRef(1);
  const latest = useRef({ historyRevision, visible, notice });
  latest.current = { historyRevision, visible, notice };

  const clearSeekTimer = useCallback(() => {
    if (seekTimer.current === null) return;
    window.clearTimeout(seekTimer.current);
    seekTimer.current = null;
  }, []);
  const cancelRead = useCallback(() => positionReader.cancel(), [positionReader]);
  const isSuspended = useCallback(() => suspended.current, []);
  const isInitialized = useCallback(() => initialized.current, []);

  const suspend = useCallback(() => {
    if (suspended.current) return;
    suspended.current = true;
    cancelRead();
    clearSeekTimer();
    setLoading(false);
    onSuspended();
  }, [cancelRead, clearSeekTimer, onSuspended]);
  const resume = useCallback(() => {
    if (!suspended.current) return;
    suspended.current = false;
    if (!latest.current.visible || latest.current.historyRevision === null) return;
    const fraction = getReadFraction(0);
    if (fraction === null) {
      setReadEpoch((epoch) => epoch + 1);
      return;
    }
    void loadWindowRef.current(fraction);
  }, [getReadFraction]);

  const canPublish = useCallback(
    (read: Promise<TranscriptPositionWindow | null>, revision: number) =>
      positionReader.isCurrent(read) &&
      !suspended.current &&
      latest.current.visible &&
      latest.current.historyRevision === revision,
    [positionReader],
  );
  const finishRead = useCallback((accepted: boolean) => {
    setLoading(false);
    if (accepted) setError(null);
  }, []);
  const failRead = useCallback((cause: unknown) => {
    setError(cause instanceof Error ? cause.message : "Transcript position read failed.");
    setLoading(false);
  }, []);

  const readWindow = useCallback(
    async (target: TranscriptPositionTarget, fraction: number) => {
      const { historyRevision: revision, visible, notice } = latest.current;
      if (suspended.current || !visible || revision === null) return;

      setLoading(true);
      const read = positionReader.read(target, notice);
      try {
        const next = await read;
        if (next === null || !canPublish(read, revision)) return;
        setWindow(next, fraction, !initialized.current, finishRead);
        initialized.current = true;
      } catch (cause) {
        if (canPublish(read, revision)) failRead(cause);
      } finally {
        // Let the viewport apply this window and its anchor before locating the next one.
        if (
          canPublish(read, revision) &&
          positionReader.shouldRefresh(revision, latest.current.notice)
        )
          setReadEpoch((epoch) => epoch + 1);
      }
    },
    [canPublish, failRead, finishRead, positionReader, setWindow],
  );
  const loadWindow = useCallback(
    async (fraction: number) => {
      const revision = latest.current.historyRevision;
      if (revision === null) return;
      desiredFraction.current = fraction;
      await readWindow({ kind: "fraction", historyRevision: revision, fraction }, fraction);
    },
    [readWindow],
  );
  loadWindowRef.current = loadWindow;

  useLayoutEffect(
    () => () => {
      cancelRead();
      clearSeekTimer();
    },
    [cancelRead, clearSeekTimer, historyRevision, root, visible],
  );

  useEffect(() => {
    if (
      initialized.current ||
      suspended.current ||
      documentInactive ||
      !root ||
      !visible ||
      historyRevision === null ||
      !positionReader.shouldRefresh(historyRevision, latest.current.notice)
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
    void readWindow(target, 1);
  }, [
    documentInactive,
    historyRevision,
    positionReader,
    readEpoch,
    readWindow,
    root,
    store,
    visible,
  ]);

  useEffect(() => {
    if (
      !hasWindow ||
      !root ||
      !visible ||
      documentInactive ||
      historyRevision === null ||
      !positionReader.shouldRefresh(historyRevision, notice)
    )
      return;
    const fraction = getReadFraction();
    if (fraction !== null) void loadWindow(fraction);
  }, [
    documentInactive,
    getReadFraction,
    hasWindow,
    historyRevision,
    loadWindow,
    notice,
    positionReader,
    readEpoch,
    root,
    visible,
  ]);

  function updateSeek(fraction: number, covered: boolean) {
    desiredFraction.current = fraction;
    // A seek already queued uses the latest fraction, even if this scroll is covered.
    if (covered) return;
    clearSeekTimer();
    seekTimer.current = window.setTimeout(() => {
      seekTimer.current = null;
      void loadWindow(desiredFraction.current);
    }, seekDebounceMs);
  }

  return {
    loading,
    error,
    retry: () => void loadWindow(desiredFraction.current),
    loadWindow,
    updateSeek,
    suspend,
    resume,
    isSuspended,
    isInitialized,
    clearSeekTimer,
    cancelRead,
  };
}
