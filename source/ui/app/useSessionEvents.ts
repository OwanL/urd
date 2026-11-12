import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { SessionEvents } from "../sessions/SessionEvents.ts";
import type { WorkspaceCommandAccess } from "./WorkspaceCommands.ts";
import { newChat } from "./WorkspaceState.ts";
import { enumerateSessionPages } from "./enumerateSessionPages.ts";
import {
  emptySessionActivity,
  recordSessionStatus,
  resetSessionTransitionStatuses,
  retainOpenSessionActivity,
} from "./SessionActivity.ts";

const maxObservedSessions = 128;

type SessionConnectionState = "connecting" | "connected" | "disconnected" | "paused";

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
  const [activity, setActivity] = useState(emptySessionActivity);
  const [connectionState, setConnectionState] = useState<SessionConnectionState>("connecting");
  const [hasConnected, setHasConnected] = useState(false);
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
    let streamStarted = false;
    let streamConnected = false;
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
        for await (const page of enumerateSessionPages(client, signal)) {
          if (signal.aborted || disposed) return;
          for (const session of page.sessionIds) add(session);
        }
      } catch (cause) {
        if (!signal.aborted && !disposed)
          setError(cause instanceof Error ? cause.message : "Session discovery failed.");
      }
    }
    const events = new SessionEvents(new URL(window.location.href), {
      connected(_boot, nextStreamId) {
        streamConnected = true;
        setStreamId(nextStreamId);
        setConnectionState("connected");
        setHasConnected(true);
        setError(null);
        discovery?.abort();
        discovery = new AbortController();
        void discover(discovery.signal);
      },
      change(session, notice) {
        setNotices((previous) => ({ ...previous, [session]: notice }));
      },
      status(session, status) {
        setActivity((previous) =>
          recordSessionStatus(previous, session, status, desired.current.active),
        );
      },
      created: add,
      disconnected() {
        streamConnected = false;
        setStreamId(null);
        setConnectionState("disconnected");
        setActivity(resetSessionTransitionStatuses);
        setNotices({});
      },
      error(message) {
        setError(message);
        if (!streamConnected) setConnectionState("disconnected");
      },
    });
    transport.current = events;
    events.update(desired.current.open, desired.current.active ? [desired.current.active] : []);
    function activate() {
      if (document.hidden) {
        suspend();
        return;
      }
      if (streamStarted) return;
      streamStarted = true;
      setConnectionState("connecting");
      events.start();
    }
    function suspend() {
      if (streamStarted) {
        streamStarted = false;
        events.stop();
      }
      discovery?.abort();
      setConnectionState("paused");
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
      streamStarted = false;
      events.stop();
      setConnectionState("disconnected");
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
    setActivity((previous) => retainOpenSessionActivity(previous, open, observed, active));
    transport.current?.update(observed, active ? [active] : []);
  }, [observed, active, watched, open]);

  const connected = connectionState === "connected";
  return {
    connected,
    connectionState,
    streamId,
    notice: watched === active && active ? (notices[active] ?? null) : null,
    unread: activity.unread,
    knownStatuses: Object.fromEntries(open.map((session) => [session, activity.statuses[session]])),
    statuses: Object.fromEntries(
      open.map((session) => [
        session,
        !observed.includes(session) || (hasConnected && !connected)
          ? "updates-paused"
          : activity.statuses[session],
      ]),
    ),
    error:
      error ??
      (open.length > observed.length
        ? `Live updates cover ${maxObservedSessions} tabs, including the active tab. Other tabs are marked Updates paused. Close tabs to restore updates for all.`
        : null),
  };
}
