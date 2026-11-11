import { useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { CompactionReceipt } from "../sessions/Compaction.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

type PendingCompaction = { operationId: string; executionId: string | null; uncertain: boolean };
type Props = {
  client: SessionClient;
  sessionId: string;
  snapshot: SessionSnapshot | null;
  selection: LlmSelection | null;
  canStart: boolean;
  onPendingChange(pending: boolean): void;
  onRefresh(): void;
};
const description = (cause: unknown) => (cause instanceof Error ? cause.message : String(cause));

export function ManualCompactionControl(props: Props) {
  const { client, sessionId, snapshot, selection, canStart, onPendingChange, onRefresh } = props;
  const [focus, setFocus] = useState("");
  const [pending, setPending] = useState<PendingCompaction | null>(null);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [noticeIsError, setNoticeIsError] = useState(false);
  const starting = useRef(false);
  const operation = useRef<string | null>(null);
  const disabled = !canStart || pending !== null || operation.current !== null;
  const pendingMessage = !pending
    ? null
    : pending.uncertain
      ? "Compaction status is uncertain."
      : pending.executionId
        ? "Compaction accepted; awaiting session update."
        : "Starting compaction…";

  useEffect(() => {
    const id = pending?.executionId;
    if (
      !id ||
      (snapshot?.unsettledExecutionId !== id && snapshot?.lastSettlement?.executionId !== id)
    )
      return;
    setPending(null);
    setError(null);
    onPendingChange(false);
  }, [
    pending?.executionId,
    snapshot?.unsettledExecutionId,
    snapshot?.lastSettlement?.executionId,
    onPendingChange,
  ]);

  // Completion details are not in the slim snapshot, including successful no-ops.
  useEffect(() => {
    const id = operation.current;
    if (!id || snapshot?.lastSettlement?.executionId !== id) return;
    const controller = new AbortController();
    void client
      .getCompactionOperation(sessionId, id, controller.signal)
      .then((receipt) => {
        if (controller.signal.aborted || operation.current !== id) return;
        operation.current = null;
        setPending(null);
        onPendingChange(false);
        setNoticeIsError(receipt?.outcome === "failed");
        setError(receipt?.error ?? `Compaction ${receipt?.outcome ?? "settled"}.`);
      })
      .catch(() => {
        if (controller.signal.aborted || operation.current !== id) return;
        operation.current = null;
        setPending(null);
        onPendingChange(false);
        setNoticeIsError(true);
        setError("Compaction settled; its details could not be read.");
      });
    return () => controller.abort();
  }, [client, sessionId, snapshot?.lastSettlement?.executionId, onPendingChange]);

  const clearPending = (message: string) => {
    operation.current = null;
    setPending(null);
    onPendingChange(false);
    setError(message);
  };
  const applyReceipt = (id: string, receipt: CompactionReceipt) => {
    if (operation.current !== id) return;
    if (receipt.outcome !== null) {
      setNoticeIsError(receipt.outcome === "failed");
      clearPending(
        `Compaction already settled (${receipt.outcome}).${receipt.error ? ` ${receipt.error}` : ""}`,
      );
      return;
    }
    setPending({ operationId: id, executionId: receipt.executionId, uncertain: false });
    setError(receipt.error ?? null);
  };

  async function start() {
    if (disabled || starting.current || !snapshot || !selection) return;
    starting.current = true;
    const operationId = crypto.randomUUID();
    operation.current = operationId;
    setPending({ operationId, executionId: null, uncertain: false });
    onPendingChange(true);
    setNoticeIsError(true);
    setError(null);
    try {
      const receipt = await client.startCompaction(sessionId, {
        operationId,
        expectedHistoryRevision: snapshot.historyRevision,
        llmSelection: { ...selection, connection: { ...selection.connection } },
        focus: focus.trim() || null,
      });
      applyReceipt(operationId, receipt);
    } catch (cause) {
      if (cause instanceof RequestError && cause.status === 409)
        clearPending("Compaction was not started: the session is busy or fenced.");
      else {
        setPending({ operationId, executionId: null, uncertain: true });
        setError(`Compaction acknowledgement uncertain: ${description(cause)}`);
      }
    } finally {
      starting.current = false;
      onRefresh();
    }
  }

  async function checkStatus() {
    const current = pending;
    if (!current?.uncertain || checking) return;
    setChecking(true);
    try {
      const receipt = await client.getCompactionOperation(sessionId, current.operationId);
      if (receipt === null) setError("No receipt found; compaction remains uncertain.");
      else applyReceipt(current.operationId, receipt);
    } catch (cause) {
      setError(`Compaction status check failed: ${description(cause)}`);
    } finally {
      setChecking(false);
      onRefresh();
    }
  }

  return (
    <div class="chat-status" aria-live="polite">
      {pendingMessage && <span>{pendingMessage} </span>}
      {error && <span class={noticeIsError ? "inline-error" : undefined}>{error} </span>}
      {pending?.uncertain && (
        <button disabled={checking} onClick={() => void checkStatus()}>
          {checking ? "Checking…" : "Check status"}
        </button>
      )}
      <details>
        <summary>Compact history</summary>
        <label>
          Focus (optional)
          <textarea
            rows={2}
            maxLength={8192}
            value={focus}
            disabled={disabled}
            onInput={(event) => setFocus(event.currentTarget.value)}
          />
        </label>
        <button disabled={disabled} onClick={() => void start()}>
          Compact now
        </button>
      </details>
    </div>
  );
}
