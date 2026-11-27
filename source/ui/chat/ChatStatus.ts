import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { submissionTextError } from "../sessions/inputs/UserMessageSubmission.ts";
import { idleChatSelection, type ChatState } from "./ChatState.ts";
import type { ChatStopState } from "./ChatCommands.ts";

export type ChatExecutionStatus =
  | "recovery-required"
  | "recovery-can-close"
  | "blocked-run"
  | "blocked"
  | "stopping"
  | "running"
  | "accepted"
  | "compacting"
  | "failed"
  | "cancelled"
  | null;

export interface ChatStatus {
  active: boolean;
  stopId: string | undefined;
  selectionLocked: boolean;
  selection: LlmSelection | null;
  textError: string | null;
  canSend: boolean;
  canContinue: boolean;
  continuationAvailable: boolean;
  canResendNotSent: boolean;
  executionStatus: ChatExecutionStatus;
  pendingStatus: "sending" | "uncertain" | null;
}

export function deriveChatStatus(
  chat: ChatState,
  snapshot: SessionSnapshot | null,
  stop: ChatStopState | undefined,
  ready: boolean,
  canPersist: boolean,
  pendingCommand = false,
  defaultSelection: LlmSelection | null = null,
): ChatStatus {
  const active = Boolean(snapshot?.unsettledExecutionId && !snapshot.admissionFenced);
  const compacting = Boolean(active && snapshot?.isCompacting);
  const stopId = stop?.pending ? stop.executionId : undefined;
  const selectionLocked = Boolean(
    active ||
    chat.pending ||
    chat.pendingContinuation ||
    chat.messageEdit?.submission ||
    pendingCommand ||
    stopId ||
    snapshot?.stopRequested ||
    snapshot?.admissionFenced ||
    chat.knownExecutionId,
  );
  const selection =
    chat.pending?.original.llmSelection ??
    chat.pendingContinuation?.original.llmSelection ??
    (active
      ? (snapshot?.latestLlmSelection ?? null)
      : idleChatSelection(chat, snapshot, defaultSelection));
  const textError = submissionTextError(chat.draft);
  const canResendNotSent = Boolean(
    snapshot &&
    canPersist &&
    !compacting &&
    !chat.pending &&
    !chat.pendingContinuation &&
    !chat.messageEdit &&
    !pendingCommand &&
    !snapshot.admissionFenced &&
    !snapshot.stopRequested &&
    !stopId &&
    (!chat.knownExecutionId ||
      snapshot.unsettledExecutionId === chat.knownExecutionId ||
      snapshot.lastSettlement?.executionId === chat.knownExecutionId),
  );
  const canSend = Boolean(
    ready &&
    snapshot &&
    selection &&
    (chat.draft.trim() || chat.draftImages.length > 0) &&
    !(chat.draftImages.length > 0 && selection.supportsImages === false) &&
    !textError &&
    canResendNotSent,
  );
  const continuationAvailable = Boolean(
    snapshot?.canContinue === true && snapshot.lastSettlement?.executionId,
  );
  const canContinue = Boolean(
    ready &&
    canPersist &&
    continuationAvailable &&
    snapshot !== null &&
    snapshot.unsettledExecutionId === null &&
    !snapshot.admissionFenced &&
    !snapshot.stopRequested &&
    selection &&
    !chat.pending &&
    !chat.pendingContinuation &&
    !chat.messageEdit &&
    !chat.knownExecutionId &&
    !stop &&
    !pendingCommand,
  );

  return {
    active,
    stopId,
    selectionLocked,
    selection,
    textError,
    canSend,
    canResendNotSent,
    executionStatus: executionStatus(snapshot, chat.knownExecutionId, stopId),
    pendingStatus: chat.pending?.status ?? null,
    canContinue,
    continuationAvailable,
  };
}

export function rateLimitRetryTime(
  status: ChatExecutionStatus,
  retryAt: string | null | undefined,
): number | null {
  if ((status !== "running" && status !== "compacting") || !retryAt) return null;
  const time = Date.parse(retryAt);
  return Number.isFinite(time) ? time : null;
}

export function rateLimitLabel(retryAt: number, now: number): string {
  const seconds = Math.ceil((retryAt - now) / 1000);
  return seconds > 0 ? `Rate limited · retrying in ${seconds}s` : "Rate limited · retrying…";
}

function executionStatus(
  snapshot: SessionSnapshot | null,
  knownExecutionId: string | null,
  stopId: string | undefined,
): ChatExecutionStatus {
  if (snapshot?.requiresRecovery)
    return snapshot.canCloseAfterFailure ? "recovery-can-close" : "recovery-required";
  if (snapshot?.admissionFenced) return snapshot.unsettledExecutionId ? "blocked-run" : "blocked";
  if (stopId || snapshot?.stopRequested) return "stopping";
  if (snapshot?.unsettledExecutionId) return snapshot.isCompacting ? "compacting" : "running";
  const settlement = snapshot?.lastSettlement;
  if (knownExecutionId && knownExecutionId !== settlement?.executionId) return "accepted";
  const outcome = settlement?.outcome;
  return outcome === "failed" || outcome === "cancelled" ? outcome : null;
}
