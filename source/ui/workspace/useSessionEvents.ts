import { useEffect, useMemo, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { SessionEvents, type AttentionKind } from "../sessions/SessionEvents.ts";
import { ToolDraftStore } from "../sessions/ToolDrafts.ts";
import type { WorkspaceCommandAccess } from "./WorkspaceCommands.ts";
import { applySessionTitle, mergeWorkspaceSessions } from "./WorkspaceState.ts";
import { enumerateSessionPages } from "../sessions/SessionPages.ts";
import {
  emptySessionActivity,
  pendingQuestionStatus,
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
  forget: (ids: Iterable<string>) => void,
  isClosed: (id: string) => boolean,
  ready: boolean,
  open: readonly string[],
  active: string | null,
  refreshSnoozed: () => void,
  playAttention: (kind: AttentionKind) => void,
) {
  const [notices, setNotices] = useState<Record<string, string>>({});
  const [draftStore] = useState(() => new ToolDraftStore());
  const [browserRefresh, setBrowserRefresh] = useState<Record<string, number>>({});
  const [activity, setActivity] = useState(emptySessionActivity);
  const [provisioning, setProvisioning] = useState<string | null>(null);
  const [connectionState, setConnectionState] = useState<SessionConnectionState>("connecting");
  const [hasConnected, setHasConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [streamId, setStreamId] = useState<string | null>(null);
  const refreshRegistration = useRef<() => void>(() => {});
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
    let visible = false;
    let connectedStream: string | null = null;
    let viewedBefore = false;
    let discovery: AbortController | null = null;
    function addSessions(sessions: Iterable<string>) {
      if (disposed) return;
      const state = access.read();
      const next = mergeWorkspaceSessions(
        state,
        [...sessions].filter((id) => !isClosed(id)),
      );
      if (next !== state) access.save(next);
    }
    async function discover(signal: AbortSignal) {
      const savedIds = Object.keys(access.read().chats);
      const listed = new Set<string>();
      try {
        for await (const page of enumerateSessionPages(client, signal)) {
          if (signal.aborted || disposed) return;
          for (const session of page.sessionIds) listed.add(session);
        }
        if (signal.aborted || disposed) return;
        addSessions(listed);
        // Only absence from a complete read is authoritative. A chat added while paging
        // may not be in this read, so compare only records that predate the read.
        forget(savedIds.filter((id) => !listed.has(id)));
      } catch (cause) {
        if (!signal.aborted && !disposed)
          setError(cause instanceof Error ? cause.message : "Session discovery failed.");
      }
    }
    function reconnectView() {
      if (!visible || connectedStream === null) return;
      setStreamId(connectedStream);
      setConnectionState("connected");
      setHasConnected(true);
      refreshSnoozed();
      setError(null);
      // Browser pages may have changed while the view wasn't registered.
      if (viewedBefore)
        setBrowserRefresh((previous) => {
          const next: Record<string, number> = {};
          for (const session of desired.current.open) next[session] = (previous[session] ?? 0) + 1;
          return next;
        });
      viewedBefore = true;
      discovery?.abort();
      discovery = new AbortController();
      void discover(discovery.signal);
    }
    function resetView() {
      discovery?.abort();
      setStreamId(null);
      setActivity(resetSessionTransitionStatuses);
      setNotices({});
    }
    const events = new SessionEvents(new URL(window.location.href), {
      connected(boot, nextStreamId) {
        draftStore.boot(boot);
        connectedStream = nextStreamId;
        reconnectView();
      },
      attention: playAttention,
      change(session, notice) {
        if (isClosed(session)) return;
        setNotices((previous) => ({ ...previous, [session]: notice }));
      },
      status(session, status, title, hasPendingUserQuestion) {
        if (status === "unavailable") {
          if (desired.current.active === session) draftStore.clear();
          forget([session]);
          return;
        }
        if (isClosed(session)) return;
        draftStore.status(session, status);
        setActivity((previous) =>
          recordSessionStatus(
            previous,
            session,
            status,
            desired.current.active,
            hasPendingUserQuestion,
          ),
        );
        const state = access.read();
        const titled = applySessionTitle(state, session, title);
        if (titled !== state) access.save(titled);
      },
      created(session) {
        if (visible) addSessions([session]);
      },
      browser(session) {
        if (!visible || isClosed(session)) return;
        setBrowserRefresh((previous) => ({
          ...previous,
          [session]: (previous[session] ?? 0) + 1,
        }));
      },
      toolDraft(draft) {
        if (isClosed(draft.session) || desired.current.active !== draft.session) return;
        draftStore.apply(draft);
      },
      snoozeChanged() {
        if (visible) refreshSnoozed();
      },
      disconnected() {
        connectedStream = null;
        resetView();
        draftStore.disconnect();
        setConnectionState(visible ? "disconnected" : "paused");
        // Reconnection replay restores an in-flight download; stale notices are dropped.
        setProvisioning(null);
      },
      provisioning(stage, detail) {
        setProvisioning(stage === "downloading" && detail ? detail : null);
      },
      error(message) {
        if (!visible) return;
        setError(message);
        if (connectedStream === null) setConnectionState("disconnected");
      },
    });
    function updateRegistration() {
      const { open, active } = desired.current;
      // Keep only the host-wide stream when hidden. No tab or transcript subscriptions.
      events.update(visible ? open : [], visible && active ? [active] : []);
    }
    refreshRegistration.current = updateRegistration;
    function activate() {
      const nextVisible = !document.hidden;
      if (visible !== nextVisible || !streamStarted) {
        visible = nextVisible;
        updateRegistration();
        if (!visible) {
          resetView();
          draftStore.clear();
          setConnectionState("paused");
        } else if (connectedStream === null) setConnectionState("connecting");
        else reconnectView();
      }
      if (streamStarted) return;
      streamStarted = true;
      events.start();
    }
    function suspend() {
      visible = false;
      streamStarted = false;
      events.stop();
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
      draftStore.disconnect();
      setConnectionState("disconnected");
      setStreamId(null);
      refreshRegistration.current = () => {};
      document.removeEventListener("visibilitychange", activate);
      document.removeEventListener("freeze", suspend);
      document.removeEventListener("resume", activate);
      window.removeEventListener("pagehide", suspend);
      window.removeEventListener("pageshow", activate);
      window.removeEventListener("focus", activate);
    };
  }, [client, access, forget, isClosed, ready, refreshSnoozed, draftStore, playAttention]);

  useEffect(() => {
    if (watched !== active) {
      setNotices({});
      setWatched(active);
    }
    draftStore.watch(active && open.includes(active) ? active : null);
    setActivity((previous) => retainOpenSessionActivity(previous, open, observed, active));
    refreshRegistration.current();
  }, [observed, active, watched, open, draftStore]);
  const connected = connectionState === "connected";
  const observedSessions = new Set(observed);
  return {
    draftStore,
    connected,
    connectionState,
    streamId,
    notice: watched === active && active ? (notices[active] ?? null) : null,
    browserRefresh,
    provisioning,
    unread: activity.unread,
    questionStatuses: Object.fromEntries(
      open.map((session) => [
        session,
        pendingQuestionStatus(activity, session, connected && observedSessions.has(session)),
      ]),
    ),
    knownStatuses: Object.fromEntries(open.map((session) => [session, activity.statuses[session]])),
    statuses: Object.fromEntries(
      open.map((session) => [
        session,
        !observedSessions.has(session) || (hasConnected && !connected)
          ? "updates-paused"
          : activity.statuses[session],
      ]),
    ),
    error:
      error ??
      (open.length > observed.length
        ? `Live updates cover ${maxObservedSessions} tabs, including the active tab. Close tabs to restore updates for all.`
        : null),
  };
}
