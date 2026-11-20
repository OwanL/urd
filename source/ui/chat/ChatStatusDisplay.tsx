import type { ChatExecutionStatus, ChatStatus } from "./ChatStatus.ts";

const executionLabels: Record<Exclude<ChatExecutionStatus, null>, string> = {
  stopping: "Stopping…",
  running: "Running…",
  accepted: "Accepted; awaiting session update.",
  compacting: "Compacting…",
};
const settlementLabels = { failed: "Execution failed.", cancelled: "Cancelled." };

interface ChatStatusDisplayProps {
  error: string | null | undefined;
  workingDirectoryNotice: string | null;
  showWorkingDirectory: boolean;
  workingDirectoryLocked: boolean;
  readError: string | null;
  onRefresh(): void;
  provisioning: string | null;
  state: Pick<
    ChatStatus,
    | "admissionFenced"
    | "unsettledWhileFenced"
    | "executionStatus"
    | "showStopRequested"
    | "settlementStatus"
    | "pendingStatus"
    | "textError"
  >;
  settlementError: string | null | undefined;
  continuationStatus: "sending" | "uncertain" | null;
  editStatus: {
    status: "loading" | "editing" | "saving" | "uncertain";
    error: string | null;
  } | null;
  checking: boolean;
  checkingContinuation: boolean;
  canPersist: boolean;
  executionSettingsSaving: boolean;
  onCheckReceipt(): void;
  onRetrySubmission(): void;
  onCheckContinuation(): void;
  onRetryContinuation(): void;
  onCheckEdit(): void;
  onRetryEdit(): void;
}

export function ChatStatusDisplay({
  error,
  workingDirectoryNotice,
  showWorkingDirectory,
  workingDirectoryLocked,
  readError,
  onRefresh,
  provisioning,
  state,
  settlementError,
  continuationStatus,
  editStatus,
  checking,
  checkingContinuation,
  canPersist,
  executionSettingsSaving,
  onCheckReceipt,
  onRetrySubmission,
  onCheckContinuation,
  onRetryContinuation,
  onCheckEdit,
  onRetryEdit,
}: ChatStatusDisplayProps) {
  return (
    <div class="chat-status" aria-live="polite">
      {error && <span class="inline-error">{error} </span>}
      {workingDirectoryNotice && !showWorkingDirectory && (
        <span class={workingDirectoryLocked ? "inline-error" : undefined}>
          {workingDirectoryNotice}
        </span>
      )}
      {readError && <button onClick={onRefresh}>Refresh</button>}
      {provisioning && <span>{provisioning} </span>}
      {state.admissionFenced && <span>Session admission is fenced. </span>}
      {state.unsettledWhileFenced && <span>Unsettled execution. </span>}
      {state.executionStatus && (
        <span class={state.executionStatus === "running" ? "chat-running-announcement" : undefined}>
          {executionLabels[state.executionStatus]}{" "}
        </span>
      )}
      {state.showStopRequested && <span>Stop requested. </span>}
      {state.settlementStatus && (
        <span class={state.settlementStatus === "failed" ? "inline-error" : undefined}>
          {state.settlementStatus === "failed"
            ? settlementError || settlementLabels.failed
            : settlementLabels.cancelled}{" "}
        </span>
      )}
      {state.pendingStatus && (
        <span>
          {state.pendingStatus === "sending"
            ? "Sending…"
            : checking
              ? "Checking receipt…"
              : "Send uncertain."}{" "}
          {state.pendingStatus === "uncertain" && (
            <>
              <button disabled={checking} onClick={onCheckReceipt}>
                Check receipt
              </button>
              <button
                disabled={!canPersist || checking || executionSettingsSaving}
                onClick={onRetrySubmission}
              >
                Retry original
              </button>
            </>
          )}
        </span>
      )}
      {continuationStatus && (
        <span>
          {continuationStatus === "sending"
            ? "Continuing… "
            : checkingContinuation
              ? "Checking Continue receipt… "
              : "Continue uncertain. "}
          {continuationStatus === "uncertain" && (
            <>
              <button disabled={checkingContinuation} onClick={onCheckContinuation}>
                Check receipt
              </button>
              <button
                disabled={!canPersist || checkingContinuation || executionSettingsSaving}
                onClick={onRetryContinuation}
              >
                Retry original
              </button>
            </>
          )}
        </span>
      )}
      {editStatus && (
        <span>
          {editStatus.status === "saving" ? "Saving edit… " : "Save uncertain. "}
          {editStatus.status === "uncertain" && (
            <>
              {editStatus.error && <span class="inline-error">{editStatus.error} </span>}
              <button onClick={onCheckEdit}>Check receipt</button>
              <button disabled={!canPersist} onClick={onRetryEdit}>
                Retry save
              </button>
            </>
          )}
        </span>
      )}
      {state.textError && <span class="inline-error">{state.textError}</span>}
    </div>
  );
}
