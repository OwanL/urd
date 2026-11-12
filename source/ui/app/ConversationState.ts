import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { submissionTextError } from "./WorkspaceState.ts";
import type { ChatState } from "./WorkspaceState.ts";
import type { WorkspaceStopState } from "./WorkspaceCommands.ts";

export type ConversationExecutionStatus = "stopping" | "running" | "accepted" | "compacting" | null;
export type ConversationSettlementStatus = "failed" | "cancelled" | null;

export interface ConversationState {
  active: boolean;
  stopId: string | undefined;
  selectionLocked: boolean;
  selection: LlmSelection | null;
  textError: string | null;
  canSend: boolean;
  canResendNotSent: boolean;
  admissionFenced: boolean;
  unsettledWhileFenced: boolean;
  executionStatus: ConversationExecutionStatus;
  showStopRequested: boolean;
  settlementStatus: ConversationSettlementStatus;
  pendingStatus: "sending" | "uncertain" | null;
}

export function deriveConversationState(
  chat: ChatState,
  snapshot: SessionSnapshot | null,
  stop: WorkspaceStopState | undefined,
  ready: boolean,
  canPersist: boolean,
): ConversationState {
  const active = Boolean(snapshot?.unsettledExecutionId && !snapshot.admissionFenced);
  const compacting = Boolean(active && snapshot?.isCompacting);
  const stopId = stop?.pending ? stop.executionId : undefined;
  const selectionLocked = Boolean(
    active ||
    chat.pending ||
    chat.messageEdit?.submission ||
    stopId ||
    snapshot?.stopRequested ||
    snapshot?.admissionFenced ||
    chat.knownExecutionId,
  );
  const selection = active
    ? (snapshot?.latestLlmSelection ?? null)
    : (chat.selection ?? snapshot?.latestLlmSelection ?? null);
  const textError = submissionTextError(chat.draft);
  const canResendNotSent = Boolean(
    snapshot &&
    canPersist &&
    !compacting &&
    !chat.pending &&
    !chat.messageEdit?.submission &&
    !snapshot.admissionFenced &&
    !snapshot.stopRequested &&
    !stopId &&
    (!chat.knownExecutionId ||
      snapshot.unsettledExecutionId === chat.knownExecutionId ||
      snapshot.lastSettlement?.executionId === chat.knownExecutionId),
  );
  const canSend = Boolean(
    ready && snapshot && selection && chat.draft.trim() && !textError && canResendNotSent,
  );
  const admissionFenced = Boolean(snapshot?.admissionFenced);
  const unsettledWhileFenced = Boolean(snapshot?.unsettledExecutionId && admissionFenced);
  const executionStatus: ConversationExecutionStatus = active
    ? snapshot?.stopRequested || stopId
      ? "stopping"
      : compacting
        ? "compacting"
        : "running"
    : chat.knownExecutionId
      ? "accepted"
      : null;
  const showStopRequested = !active && Boolean(snapshot?.stopRequested);
  let settlementStatus: ConversationSettlementStatus = null;
  if (!active && !chat.knownExecutionId && !stopId && !snapshot?.stopRequested) {
    if (snapshot?.lastSettlement?.outcome === "failed") settlementStatus = "failed";
    else if (snapshot?.lastSettlement?.outcome === "cancelled") settlementStatus = "cancelled";
  }

  return {
    active,
    stopId,
    selectionLocked,
    selection,
    textError,
    canSend,
    canResendNotSent,
    admissionFenced,
    unsettledWhileFenced,
    executionStatus,
    showStopRequested,
    settlementStatus,
    pendingStatus: chat.pending?.status ?? null,
  };
}
