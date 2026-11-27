export interface SessionActivityState {
  readonly statuses: Readonly<Record<string, string>>;
  readonly transitionStatuses: Readonly<Record<string, string>>;
  readonly unread: ReadonlySet<string>;
  readonly pendingQuestions: ReadonlySet<string>;
}

export const emptySessionActivity: SessionActivityState = {
  statuses: {},
  transitionStatuses: {},
  unread: new Set(),
  pendingQuestions: new Set(),
};

export function isBusySessionStatus(status: string | undefined): boolean {
  return status === "running" || status === "waiting" || status === "compacting";
}

export type PendingQuestionStatus = "pending" | "last-known";

export function pendingQuestionStatus(
  activity: SessionActivityState,
  session: string,
  connected: boolean,
): PendingQuestionStatus | undefined {
  if (!activity.pendingQuestions.has(session)) return;
  return connected && activity.transitionStatuses[session] !== undefined ? "pending" : "last-known";
}

export function recordSessionStatus(
  previous: SessionActivityState,
  session: string,
  status: string,
  active: string | null,
  hasPendingUserQuestion = false,
): SessionActivityState {
  const unread = new Set(previous.unread);
  if (session === active || isBusySessionStatus(status)) unread.delete(session);
  else if (status === "idle" && isBusySessionStatus(previous.transitionStatuses[session]))
    unread.add(session);

  const pendingQuestions = new Set(previous.pendingQuestions);
  if (hasPendingUserQuestion) pendingQuestions.add(session);
  else pendingQuestions.delete(session);

  return {
    statuses: { ...previous.statuses, [session]: status },
    transitionStatuses: { ...previous.transitionStatuses, [session]: status },
    unread,
    pendingQuestions,
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
    pendingQuestions: new Set(
      [...previous.pendingQuestions].filter((session) => openSessions.has(session)),
    ),
  };
}
