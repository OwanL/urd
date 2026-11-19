export interface SessionActivityState {
  readonly statuses: Readonly<Record<string, string>>;
  readonly transitionStatuses: Readonly<Record<string, string>>;
  readonly unread: ReadonlySet<string>;
}

export const emptySessionActivity: SessionActivityState = {
  statuses: {},
  transitionStatuses: {},
  unread: new Set(),
};

export function isBusySessionStatus(status: string | undefined): boolean {
  return status === "running" || status === "waiting" || status === "compacting";
}

export function recordSessionStatus(
  previous: SessionActivityState,
  session: string,
  status: string,
  active: string | null,
): SessionActivityState {
  const unread = new Set(previous.unread);
  if (session === active || isBusySessionStatus(status)) unread.delete(session);
  else if (status === "idle" && isBusySessionStatus(previous.transitionStatuses[session]))
    unread.add(session);

  return {
    statuses: { ...previous.statuses, [session]: status },
    transitionStatuses: { ...previous.transitionStatuses, [session]: status },
    unread,
  };
}

export function resetSessionTransitionStatuses(
  previous: SessionActivityState,
): SessionActivityState {
  return { ...previous, transitionStatuses: {} };
}

export function retainOpenSessionActivity(
  previous: SessionActivityState,
  open: readonly string[],
  observed: readonly string[],
  active: string | null,
): SessionActivityState {
  const openSessions = new Set(open);
  const observedSessions = new Set(observed);
  return {
    statuses: Object.fromEntries(
      Object.entries(previous.statuses).filter(
        ([session, status]) =>
          observedSessions.has(session) ||
          (openSessions.has(session) && isBusySessionStatus(status)),
      ),
    ),
    transitionStatuses: Object.fromEntries(
      Object.entries(previous.transitionStatuses).filter(([session]) =>
        observedSessions.has(session),
      ),
    ),
    unread: new Set(
      [...previous.unread].filter((session) => openSessions.has(session) && session !== active),
    ),
  };
}
