import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { SessionEvents } from "../sessions/SessionEvents.ts";
import type { WorkspaceCommandAccess } from "./WorkspaceCommands.ts";
import { newChat } from "./WorkspaceState.ts";

const maxObservedSessions = 128;

// Page lifetime owns the stream; mounted conversations only consume handshakes.
export function useSessionEvents(
  client: SessionClient,
  access: WorkspaceCommandAccess,
  discovered: Set<string>,
  ready: boolean,
  open: readonly string[],
  active: string | null,
) {
  const [notices, setNotices] = useState<Record<string, string>>({});
  const [statuses, setStatuses] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [streamId, setStreamId] = useState<string | null>(null);
  const transport = useRef<SessionEvents | null>(null);
  // Match the server's per-page bound without dropping tabs, drafts or the active view.
  const observed = useMemo(
    () =>
      open.length <= maxObservedSessions
        ? open
        : [...new Set([...(active ? [active] : []), ...open])].slice(0, maxObservedSessions),
    [open, active],
  );
  const desired = useRef({ open: observed, active });
  desired.current = { open: observed, active };
  const [watched, setWatched] = useState<string | null>(null);

  useEffect(() => {
    if (!ready) return;
    let disposed = false;
    let discovery: AbortController | null = null;
    function add(session: string) {
      if (disposed || discovered.has(session)) return;
      discovered.add(session);
      const state = access.read();
      // A closed tab remains known and is not reopened by reconnection discovery.
      if (state.chats[session]) return;
      access.save({
        ...state,
        tabs: [...state.tabs, session],
        chats: { ...state.chats, [session]: newChat(null) },
      });
    }
    async function discover(signal: AbortSignal) {
      try {
        let cursor: string | undefined;
        do {
          const page = await client.list(cursor, signal);
          if (signal.aborted || disposed) return;
          for (const session of page.sessionIds) add(session);
          cursor = page.nextCursor ?? undefined;
        } while (cursor !== undefined);
      } catch (cause) {
        if (!signal.aborted && !disposed)
          setError(cause instanceof Error ? cause.message : "Session discovery failed.");
      }
    }
    const events = new SessionEvents(new URL(window.location.href), {
      connected(_boot, nextStreamId) {
        setStreamId(nextStreamId);
        setError(null);
        discovery?.abort();
        discovery = new AbortController();
        void discover(discovery.signal);
      },
      change(session, notice) {
        setNotices((previous) => ({ ...previous, [session]: notice }));
      },
      status(session, status) {
        setStatuses((previous) => ({ ...previous, [session]: status }));
      },
      created: add,
      disconnected() {
        setStreamId(null);
        setNotices({});
        setStatuses({});
      },
      error: setError,
    });
    transport.current = events;
    events.update(desired.current.open, desired.current.active ? [desired.current.active] : []);
    function activate() {
      if (document.hidden) {
        events.stop();
        discovery?.abort();
      } else events.start();
    }
    function suspend() {
      events.stop();
      discovery?.abort();
    }
    document.addEventListener("visibilitychange", activate);
    document.addEventListener("freeze", suspend);
    document.addEventListener("resume", activate);
    window.addEventListener("pagehide", suspend);
    window.addEventListener("pageshow", activate);
    window.addEventListener("focus", activate);
    activate();
    return () => {
      disposed = true;
      discovery?.abort();
      events.stop();
      setStreamId(null);
      transport.current = null;
      document.removeEventListener("visibilitychange", activate);
      document.removeEventListener("freeze", suspend);
      document.removeEventListener("resume", activate);
      window.removeEventListener("pagehide", suspend);
      window.removeEventListener("pageshow", activate);
      window.removeEventListener("focus", activate);
    };
  }, [client, access, discovered, ready]);

  useEffect(() => {
    if (watched !== active) {
      setNotices({});
      setWatched(active);
    }
    setStatuses((previous) =>
      Object.fromEntries(
        Object.entries(previous).filter(([session]) => observed.includes(session)),
      ),
    );
    transport.current?.update(observed, active ? [active] : []);
  }, [observed, active, watched]);

  return {
    streamId,
    notice: watched === active && active ? (notices[active] ?? null) : null,
    statuses: Object.fromEntries(
      open.map((session) => [
        session,
        observed.includes(session) ? statuses[session] : "updates-paused",
      ]),
    ),
    error:
      error ??
      (open.length > observed.length
        ? `Live updates cover ${maxObservedSessions} tabs, including the active tab. Other tabs are marked Updates paused. Close tabs to restore updates for all.`
        : null),
  };
}
