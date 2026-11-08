import { useEffect, useState } from "preact/hooks";
import { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

export function useSessionSnapshot(
  client: SessionClient,
  sessionId: string | null,
  refresh: number,
  knownExecutionId: string | null,
  sending: boolean,
  onSnapshot: (snapshot: SessionSnapshot, knownWhenRead: string | null) => void,
) {
  const [snapshot, setSnapshot] = useState<SessionSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [visible, setVisible] = useState(!document.hidden);
  useEffect(() => {
    setError(null);
    if (!sessionId) return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | null = null;
    async function read() {
      if (disposed || document.hidden || controller) return;
      const request = new AbortController();
      controller = request;
      try {
        const next = await client.getSnapshot(sessionId!, request.signal);
        if (request.signal.aborted || disposed) return;
        if (next.sessionId !== sessionId || !Array.isArray(next.messages))
          throw new Error("Invalid session snapshot.");
        setSnapshot(next);
        setError(null);
        onSnapshot(next, knownExecutionId);
        const waiting = knownExecutionId && next.lastSettlement?.executionId !== knownExecutionId;
        if (
          !next.admissionFenced &&
          (next.unsettledExecutionId || next.stopRequested || waiting || sending)
        )
          timer = setTimeout(() => {
            void read();
          }, 350);
      } catch (cause) {
        if (!request.signal.aborted && !disposed)
          setError(cause instanceof Error ? cause.message : "Session read failed.");
      } finally {
        if (controller === request) controller = null;
      }
    }
    function activate() {
      setVisible(!document.hidden);
      clearTimeout(timer);
      controller?.abort();
      controller = null;
      if (!document.hidden) void read();
    }
    document.addEventListener("visibilitychange", activate);
    window.addEventListener("focus", activate);
    void read();
    return () => {
      disposed = true;
      clearTimeout(timer);
      controller?.abort();
      document.removeEventListener("visibilitychange", activate);
      window.removeEventListener("focus", activate);
    };
  }, [client, sessionId, refresh, knownExecutionId, sending, onSnapshot]);
  return { snapshot: snapshot?.sessionId === sessionId ? snapshot : null, error, visible };
}
