import { useEffect, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { TranscriptHistoryPage } from "../sessions/Messages.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

export function useSessionSnapshot(
  client: SessionClient,
  sessionId: string | null,
  refresh: number,
  knownExecutionId: string | null,
  sending: boolean,
  onSnapshot: (snapshot: SessionSnapshot, knownWhenRead: string | null) => void,
  notice: string | null,
  enabled = true,
  includeHistory = true,
) {
  const [snapshot, setSnapshot] = useState<SessionSnapshot | null>(null);
  const [history, setHistory] = useState<TranscriptHistoryPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [visible, setVisible] = useState(!document.hidden);
  const requestRead = useRef<() => void>(() => {});
  const current = useRef({ knownExecutionId, onSnapshot, notice, sending });
  current.current = { knownExecutionId, onSnapshot, notice, sending };

  useEffect(() => {
    setError(null);
    if (!sessionId || !enabled) return;
    const requestedSessionId = sessionId;

    let disposed = false;
    let reading = false;
    let queued = false;
    let suspended = false;
    let controller: AbortController | null = null;

    async function read() {
      if (disposed || suspended || current.current.notice === null) return;
      if (document.hidden) {
        queued = true;
        return;
      }
      if (reading) {
        queued = true;
        return;
      }

      reading = true;
      const request = new AbortController();
      controller = request;
      const knownWhenRead = current.current.knownExecutionId;
      try {
        const next = await client.getSnapshot(requestedSessionId, request.signal);
        if (request.signal.aborted || disposed) return;
        if (next.sessionId !== requestedSessionId || !Number.isSafeInteger(next.historyRevision))
          throw new Error("Invalid session snapshot.");

        const nextHistory = includeHistory
          ? await client.getHistory(
              requestedSessionId,
              { mode: "latest", expectedHistoryRevision: next.historyRevision },
              request.signal,
            )
          : null;
        if (request.signal.aborted || disposed) return;
        if (
          nextHistory &&
          (nextHistory.sessionId !== requestedSessionId ||
            nextHistory.historyRevision !== next.historyRevision ||
            !Array.isArray(nextHistory.entries))
        )
          throw new Error("Invalid session history.");

        setSnapshot(next);
        setHistory(nextHistory);
        setError(null);
        current.current.onSnapshot(next, knownWhenRead);
      } catch (cause) {
        if (!request.signal.aborted && !disposed)
          setError(cause instanceof Error ? cause.message : "Session read failed.");
      } finally {
        if (controller === request) controller = null;
        reading = false;
        if (queued && !disposed && current.current.notice !== null) {
          queued = false;
          void read();
        }
      }
    }

    requestRead.current = () => {
      if (current.current.notice === null) return;
      if (suspended) {
        queued = true;
        return;
      }
      if (reading) {
        queued = true;
        return;
      }
      void read();
    };

    function suspend() {
      suspended = true;
      queued = false;
      controller?.abort();
    }
    function resume() {
      if (document.hidden) return;
      suspended = false;
      setVisible(true);
      requestRead.current();
    }
    function activate() {
      setVisible(!document.hidden);
      if (document.hidden) suspend();
      else requestRead.current();
    }

    document.addEventListener("visibilitychange", activate);
    window.addEventListener("focus", activate);
    window.addEventListener("pagehide", suspend);
    window.addEventListener("pageshow", resume);
    document.addEventListener("freeze", suspend as EventListener);
    document.addEventListener("resume", resume as EventListener);
    return () => {
      disposed = true;
      queued = false;
      controller?.abort();
      document.removeEventListener("visibilitychange", activate);
      window.removeEventListener("focus", activate);
      window.removeEventListener("pagehide", suspend);
      window.removeEventListener("pageshow", resume);
      document.removeEventListener("freeze", suspend as EventListener);
      document.removeEventListener("resume", resume as EventListener);
      requestRead.current = () => {};
    };
  }, [client, sessionId, enabled, includeHistory]);

  useEffect(() => {
    if (notice !== null) requestRead.current();
  }, [notice, refresh, enabled]);

  return {
    snapshot: snapshot?.sessionId === sessionId ? snapshot : null,
    history: history?.sessionId === sessionId ? history : null,
    error,
    visible: enabled && visible,
  };
}
