import { useEffect, useState } from "preact/hooks";
import { rateLimitLabel } from "./ChatStatus.ts";
import type { ChatExecutionStatus, ChatStatus } from "./ChatStatus.ts";

const executionLabels: Record<Exclude<ChatExecutionStatus, null>, string> = {
  "recovery-required":
    "Urd couldn’t safely finish this chat’s last operation. New messages are blocked. Restart Urd once your other chats are idle.",
  "recovery-can-close":
    "The run ended, but Urd couldn’t save its final status. Restart Urd to keep this chat, or close it to delete it permanently.",
  "blocked-run": "The last run hasn’t finished. New messages are blocked.",
  blocked: "This chat can’t accept new messages right now.",
  stopping: "Stopping…",
  running: "Running…",
  accepted: "Request accepted. Waiting for a status update…",
  compacting: "Compacting…",
  failed: "Run failed.",
  cancelled: "Cancelled.",
};

function RateLimitStatus({ retryAt }: { retryAt: number }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (now >= retryAt) return;
    const timer = window.setTimeout(() => setNow(Date.now()), Math.min(1000, retryAt - now));
    return () => window.clearTimeout(timer);
  }, [now, retryAt]);

  return (
    <span>
      <span class="chat-running-announcement">Rate limited · retrying</span>
      <span aria-hidden="true">{rateLimitLabel(retryAt, now)}</span>{" "}
    </span>
  );
}

interface ChatStatusDisplayProps {
  error: string | null | undefined;
  workingDirectoryNotice: string | null;
  showWorkingDirectory: boolean;
  workingDirectoryLocked: boolean;
  readError: string | null;
  onRefresh(): void;
  provisioning: string | null;
  state: Pick<ChatStatus, "executionStatus" | "pendingStatus" | "textError">;
  settlementError: string | null | undefined;
  rateLimitRetryTime: number | null;
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
  rateLimitRetryTime,
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
  const status = state.executionStatus;
  const canCheckStatus = status === "blocked" || status === "blocked-run";
  const statusClass =
    status === "running"
      ? "chat-running-announcement"
      : status === "recovery-required" || status === "recovery-can-close" || status === "failed"
        ? "inline-error"
        : undefined;
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
      {rateLimitRetryTime !== null ? (
        <RateLimitStatus key={rateLimitRetryTime} retryAt={rateLimitRetryTime} />
      ) : (
        status && (
          <span class={statusClass}>
            {status === "failed"
              ? settlementError || executionLabels.failed
              : executionLabels[status]}{" "}
          </span>
        )
      )}
      {canCheckStatus && !readError && <button onClick={onRefresh}>Check status</button>}
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
