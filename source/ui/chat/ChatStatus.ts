import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { submissionTextError } from "../sessions/inputs/UserMessageSubmission.ts";
import { idleChatSelection, type ChatState } from "./ChatState.ts";
import type { ChatStopState } from "./ChatCommands.ts";

export type ChatExecutionStatus = "stopping" | "running" | "accepted" | "compacting" | null;
export type ChatSettlementStatus = "failed" | "cancelled" | null;

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
  admissionFenced: boolean;
  unsettledWhileFenced: boolean;
  executionStatus: ChatExecutionStatus;
  showStopRequested: boolean;
  settlementStatus: ChatSettlementStatus;
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
    chat.messageEdit ||
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
    ready && snapshot && selection && chat.draft.trim() && !textError && canResendNotSent,
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
  const admissionFenced = Boolean(snapshot?.admissionFenced);
  const unsettledWhileFenced = Boolean(snapshot?.unsettledExecutionId && admissionFenced);
  const executionStatus: ChatExecutionStatus = active
    ? snapshot?.stopRequested || stopId
      ? "stopping"
      : compacting
        ? "compacting"
        : "running"
    : chat.knownExecutionId
      ? "accepted"
      : null;
  const showStopRequested = !active && Boolean(snapshot?.stopRequested);
  let settlementStatus: ChatSettlementStatus = null;
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
    canContinue,
    continuationAvailable,
  };
}
