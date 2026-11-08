import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { submissionTextError } from "./WorkspaceState.ts";
import type { ChatState } from "./WorkspaceState.ts";
import type { WorkspaceStopState } from "./WorkspaceCommands.ts";

export type ConversationExecutionStatus = "stopping" | "running" | "accepted" | null;
export type ConversationSettlementStatus = "failed" | "cancelled" | null;

export interface ConversationState {
  active: boolean;
  stopId: string | undefined;
  selectionLocked: boolean;
  selection: LlmSelection | null;
  textError: string | null;
  canSend: boolean;
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
  const stopId = stop?.pending ? stop.executionId : undefined;
  const selectionLocked = Boolean(
    active ||
    chat.pending ||
    stopId ||
    snapshot?.stopRequested ||
    snapshot?.admissionFenced ||
    chat.knownExecutionId,
  );
  const selection = active
    ? (snapshot?.latestLlmSelection ?? null)
    : (chat.selection ?? snapshot?.latestLlmSelection ?? null);
  const textError = submissionTextError(chat.draft);
  const canSend = Boolean(
    ready &&
    snapshot &&
    selection &&
    chat.draft.trim() &&
    !textError &&
    !chat.pending &&
    (!chat.knownExecutionId ||
      snapshot.unsettledExecutionId === chat.knownExecutionId ||
      snapshot.lastSettlement?.executionId === chat.knownExecutionId) &&
    !snapshot.stopRequested &&
    !snapshot.admissionFenced &&
    !stopId &&
    canPersist,
  );
  const admissionFenced = Boolean(snapshot?.admissionFenced);
  const unsettledWhileFenced = Boolean(snapshot?.unsettledExecutionId && admissionFenced);
  const executionStatus: ConversationExecutionStatus = active
    ? snapshot?.stopRequested || stopId
      ? "stopping"
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
    admissionFenced,
    unsettledWhileFenced,
    executionStatus,
    showStopRequested,
    settlementStatus,
    pendingStatus: chat.pending?.status ?? null,
  };
}
