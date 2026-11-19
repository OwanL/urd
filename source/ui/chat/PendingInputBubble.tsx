import { useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../controls/IconButton.tsx";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { PendingInputRemovalState } from "./ChatCommands.ts";

export function PendingInputBubble({
  client,
  sessionId,
  submissionId,
  preview,
  removalStatus,
  removeLabel,
  checkLabel,
  disabled = false,
  onRemove,
  onCheck,
}: {
  client?: SessionClient;
  sessionId?: string;
  submissionId?: string;
  preview: string | null;
  removalStatus: PendingInputRemovalState["status"] | undefined;
  removeLabel?: string | undefined;
  checkLabel?: string | undefined;
  disabled?: boolean;
  onRemove?: (() => void) | undefined;
  onCheck?: (() => void) | undefined;
}) {
  const previewElement = useRef<HTMLDivElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [canExpand, setCanExpand] = useState(false);
  const [fullText, setFullText] = useState<string | null>(null);
  const [loadStatus, setLoadStatus] = useState<"loading" | "failed" | null>(null);
  const [retry, setRetry] = useState(0);
  // A supplementary scalar may leave the 256-unit preview at 255 units.
  const mayBeTruncated = preview !== null && preview.length >= 255;

  useEffect(() => {
    if (!expanded || !mayBeTruncated || fullText !== null || !client || !sessionId || !submissionId)
      return;
    const controller = new AbortController();
    setLoadStatus("loading");
    void client.getPendingInputText(sessionId, submissionId, controller.signal).then(
      (text) => {
        if (!controller.signal.aborted) {
          setFullText(text);
          setLoadStatus(null);
        }
      },
      () => {
        if (!controller.signal.aborted) setLoadStatus("failed");
      },
    );
    return () => controller.abort();
  }, [client, sessionId, submissionId, expanded, mayBeTruncated, fullText, retry]);

  useLayoutEffect(() => {
    const node = previewElement.current;
    if (!node || preview === null || expanded) return;

    const updateOverflow = () => setCanExpand(node.scrollHeight > node.clientHeight + 1);
    updateOverflow();
    const observer = new ResizeObserver(updateOverflow);
    observer.observe(node);
    return () => observer.disconnect();
  }, [expanded, preview]);

  const status =
    removalStatus === "removing"
      ? "Removing…"
      : removalStatus === "checking"
        ? "Checking removal…"
        : removalStatus === "uncertain"
          ? "Removal status unknown."
          : null;

  return (
    <div class={`chat-pending-input${removalStatus ? " has-removal-state" : ""}`}>
      <div
        ref={previewElement}
        role={preview === null ? "status" : undefined}
        class={`chat-pending-input-preview message-text${expanded ? " is-expanded" : ""}`}
      >
        {(expanded ? fullText : null) ?? preview ?? status}
      </div>
      <div class="chat-pending-input-actions">
        {(canExpand || mayBeTruncated) && (
          <IconButton
            icon="chevron"
            label={`${expanded ? "Collapse" : "Expand"} input preview`}
            hint={`${expanded ? "Collapse" : "Expand"} preview`}
            class={`chat-pending-input-expand${expanded ? " is-expanded" : ""}`}
            aria-expanded={expanded}
            onClick={() => setExpanded((value) => !value)}
          />
        )}
        {removalStatus === "uncertain" && onCheck && (
          <button
            type="button"
            class="chat-pending-input-check"
            aria-label={checkLabel}
            onClick={onCheck}
          >
            Check
          </button>
        )}
        {!removalStatus && removeLabel && onRemove && (
          <IconButton
            icon="close"
            label={removeLabel}
            hint="Remove queued message"
            class="chat-pending-input-remove"
            disabled={disabled}
            onClick={onRemove}
          />
        )}
      </div>
      {expanded && mayBeTruncated && fullText === null && (
        <span class="chat-pending-input-status" role="status">
          {loadStatus === "failed" ? (
            <>
              Input unavailable.{" "}
              <button onClick={() => setRetry((value) => value + 1)}>Retry</button>
            </>
          ) : (
            "Loading input…"
          )}
        </span>
      )}
      {preview !== null && status && (
        <span class="chat-pending-input-status" role="status">
          {status}
        </span>
      )}
    </div>
  );
}
