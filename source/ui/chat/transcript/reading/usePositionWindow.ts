import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { TranscriptPositionWindow } from "../../../sessions/transcript/Messages.ts";
import type { TranscriptNavigationStore } from "../navigation/useTranscriptNavigation.ts";
import { TranscriptPositionReader } from "./TranscriptPositionReader.ts";

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
  const initialReadNotice = useRef<string | null>(null);
  const seekTimer = useRef<number | null>(null);
  const desiredFraction = useRef(1);
  const latest = useRef({ historyRevision, visible, notice });
  latest.current = { historyRevision, visible, notice };

  const clearSeekTimer = useCallback(() => {
    if (seekTimer.current !== null) window.clearTimeout(seekTimer.current);
  }, []);
  const cancelRead = useCallback(() => positionReader.cancel(), [positionReader]);
  const isSuspended = useCallback(() => suspended.current, []);
  const isInitialized = useCallback(() => initialized.current, []);

  const suspend = useCallback(() => {
    if (suspended.current) return;
    suspended.current = true;
    cancelRead();
    clearSeekTimer();
    seekTimer.current = null;
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

  const loadWindow = useCallback(
    async (fraction: number) => {
      const { historyRevision: revision, visible, notice } = latest.current;
      if (suspended.current || !visible || revision === null) return;

      desiredFraction.current = fraction;
      setLoading(true);
      const read = positionReader.read({ kind: "fraction", historyRevision: revision, fraction });
      try {
        const next = await read;
        if (next === null || !canPublish(read, revision)) return;
        if (!initialized.current) initialReadNotice.current = notice;
        setWindow(next, fraction, !initialized.current, finishRead);
        initialized.current = true;
      } catch (cause) {
        if (canPublish(read, revision)) failRead(cause);
      }
    },
    [canPublish, failRead, finishRead, positionReader, setWindow],
  );
  loadWindowRef.current = loadWindow;

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
    const noticeWhenRead = latest.current.notice;
    const read = positionReader.read(target);
    void read
      .then((next) => {
        if (next === null || !canPublish(read, historyRevision)) return;
        initialReadNotice.current = noticeWhenRead;
        setWindow(next, 1, true, finishRead);
        initialized.current = true;
      })
      .catch((cause: unknown) => {
        if (canPublish(read, historyRevision)) failRead(cause);
      });
    return cancelRead;
  }, [
    canPublish,
    cancelRead,
    documentInactive,
    failRead,
    finishRead,
    historyRevision,
    positionReader,
    readEpoch,
    root,
    sessionId,
    setWindow,
    store,
    visible,
  ]);

  useEffect(() => {
    if (!hasWindow || !visible || historyRevision === null || notice === null) return;
    // Revisit notices that arrived before the first window was published, but don't reread it otherwise.
    const coveredNotice = initialReadNotice.current;
    initialReadNotice.current = null;
    if (coveredNotice === notice) return;
    const fraction = getReadFraction();
    if (fraction !== null) void loadWindow(fraction);
  }, [getReadFraction, hasWindow, historyRevision, loadWindow, notice, visible]);

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
