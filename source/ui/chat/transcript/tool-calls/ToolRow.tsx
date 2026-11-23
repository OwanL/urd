import { useEffect, useRef, useState } from "preact/hooks";
import type { TranscriptToolFacts, ToolCallLabel } from "../../../sessions/transcript/Messages.ts";
import { Icon, type IconName } from "../../../controls/Icon.tsx";
import { useToolRowInteractions } from "./ToolRowInteractions.tsx";
import { canReviewMcpConnection } from "./McpConnectionReview.ts";
import {
  formatElapsedTime,
  toolElapsedMilliseconds,
  toolRowStatus,
  type ToolRowStatus,
} from "./ToolRowPresentation.ts";
import "./tool-row.css";

const clockSubscribers = new Set<(now: number) => void>();
let clockTimer: number | null = null;

function subscribeToToolRowClock(update: (now: number) => void): () => void {
  clockSubscribers.add(update);
  if (clockTimer === null) {
    clockTimer = window.setInterval(() => {
      const now = Date.now();
      for (const subscriber of clockSubscribers) subscriber(now);
    }, 1_000);
  }
  return () => {
    clockSubscribers.delete(update);
    if (clockSubscribers.size === 0 && clockTimer !== null) {
      window.clearInterval(clockTimer);
      clockTimer = null;
    }
  };
}

const iconNames: Record<ToolCallLabel["icon"], IconName> = {
  terminal: "terminal",
  read: "file-read",
  skill: "book-open",
  write: "file-write",
  edit: "edit",
  listPending: "list",
  cancelPending: "cancel",
  askAgent: "ask",
  answerAgent: "return",
  generic: "tool",
};

const iconLabels: Record<ToolCallLabel["icon"], string> = {
  terminal: "Terminal",
  read: "Read file",
  skill: "Load skill",
  write: "Write file",
  edit: "Edit file",
  listPending: "List pending calls",
  cancelPending: "Cancel pending call",
  askAgent: "Ask agent",
  answerAgent: "Answer agent",
  generic: "Tool",
};

const statusLabels: Record<ToolRowStatus, string> = {
  completed: "Completed",
  failed: "Failed",
  cancelled: "Cancelled",
  interrupted: "Interrupted",
  unknown: "Unknown",
  "not-run": "Not run",
  pending: "Pending",
  running: "Running",
};

export function ToolRow({
  tool,
  label,
  completion = false,
  root,
  paused,
}: {
  tool: TranscriptToolFacts;
  label: ToolCallLabel | null;
  completion?: boolean;
  root: HTMLElement | null;
  paused: boolean;
}) {
  const row = useRef<HTMLDivElement>(null);
  const icon = useRef<HTMLButtonElement>(null);
  const titleNode = useRef<HTMLButtonElement>(null);
  const interactions = useToolRowInteractions();
  const [now, setNow] = useState(() => Date.now());
  const status = toolRowStatus(tool, completion);
  const running = status === "running";
  const failed = status === "failed";
  const payloadFinished = tool.isClosed && tool.outcome !== null;
  const isAskUser = tool.name === "ask_user";
  const iconName = completion
    ? "return"
    : isAskUser
      ? "ask"
      : label
        ? iconNames[label.icon]
        : "tool";
  const iconLabel = completion
    ? "Returned tool output"
    : isAskUser
      ? "Question"
      : label
        ? iconLabels[label.icon]
        : (tool.name ?? "Tool");
  const title = isAskUser ? "Question" : (label?.title ?? tool.name ?? "Tool");
  const detail = isAskUser ? null : (label?.detail ?? null);
  const isMono = !isAskUser && (label?.icon === "terminal" || label?.icon === "generic");
  const measuredElapsed = toolElapsedMilliseconds(tool, now, completion);
  const timeText = measuredElapsed === null ? null : formatElapsedTime(measuredElapsed);
  const showBackground = tool.background && !completion;
  const path = label?.path;
  const titleClass = `tool-row-title${isMono ? " tool-row-mono" : ""}`;
  const batchedWithPrevious = tool.batchedWithPrevious;
  const batchedWithNext = tool.batchedWithNext;
  const rowClass = [
    "tool-row is-call-row",
    batchedWithPrevious || batchedWithNext ? "is-batched" : "",
  ]
    .filter(Boolean)
    .join(" ");

  const sourceChanged = interactions.sourceChanged;
  useEffect(() => {
    if (icon.current) sourceChanged(icon.current, payloadFinished);
  }, [payloadFinished, sourceChanged, tool.outcome, tool.isClosed, tool.endedAt]);

  const sourceUnmounted = interactions.sourceUnmounted;
  useEffect(() => {
    const source = icon.current;
    if (!source) return;
    return () => sourceUnmounted(source);
  }, [sourceUnmounted]);

  useEffect(() => {
    const source = titleNode.current;
    if (!source || !path) return;
    return () => sourceUnmounted(source);
  }, [path, sourceUnmounted]);

  useEffect(() => {
    const node = row.current;
    if (!running || paused || !node || !root) return;
    function startClock() {
      setNow(Date.now());
      return subscribeToToolRowClock(setNow);
    }
    if (typeof IntersectionObserver === "undefined") return startClock();

    let watching = true;
    let stopClock: (() => void) | undefined;
    const observer = new IntersectionObserver(
      ([intersection]) => {
        if (!watching) return;
        if (intersection?.isIntersecting) {
          stopClock ??= startClock();
        } else {
          stopClock?.();
          stopClock = undefined;
        }
      },
      { root },
    );
    observer.observe(node);
    return () => {
      watching = false;
      observer.disconnect();
      stopClock?.();
    };
  }, [paused, root, running]);

  return (
    <div
      ref={row}
      class={rowClass}
      data-tool-row-kind={completion ? "completion" : "call"}
      data-tool-name={tool.name ?? ""}
      data-tool-request-id={tool.requestId}
      data-tool-call-id={tool.callId}
      data-tool-outcome={tool.outcome ?? ""}
      data-tool-status={status}
      data-tool-background={tool.background}
      data-batched-with-previous={batchedWithPrevious}
      data-batched-with-next={batchedWithNext}
    >
      <button
        ref={icon}
        type="button"
        class={`tool-row-icon${failed ? " is-failed" : ""}`}
        aria-label={`Inspect ${iconLabel === title ? title : `${iconLabel}: ${title}`}, ${statusLabels[status]}`}
        aria-haspopup="dialog"
        aria-expanded={icon.current ? interactions.isOpen(icon.current, "inspection") : false}
        data-tool-icon={iconName}
        onClick={(event) => {
          interactions.openInspection(
            event.currentTarget,
            tool.requestId,
            tool.callId,
            completion,
            iconLabel,
            payloadFinished,
            true,
          );
        }}
        onPointerEnter={(event) => {
          if (event.pointerType === "touch") return;
          interactions.scheduleInspection(
            event.currentTarget,
            tool.requestId,
            tool.callId,
            completion,
            iconLabel,
            payloadFinished,
          );
        }}
        onPointerLeave={(event) => interactions.leaveSource(event.currentTarget)}
      >
        <Icon name={iconName} />
      </button>
      <div class="tool-row-content">
        {path ? (
          <button
            ref={titleNode}
            class={titleClass}
            type="button"
            title={path}
            aria-label={`File actions: ${title}`}
            aria-haspopup="menu"
            aria-expanded={
              titleNode.current ? interactions.isOpen(titleNode.current, "menu") : false
            }
            data-tool-title=""
            data-tool-path={path}
            onClick={(event) => interactions.openFileMenu(event.currentTarget, path)}
            onContextMenu={(event) => {
              event.preventDefault();
              interactions.openFileMenu(event.currentTarget, path);
            }}
            onKeyDown={(event) => {
              if (event.key !== "ContextMenu" && !(event.key === "F10" && event.shiftKey)) return;
              event.preventDefault();
              interactions.openFileMenu(event.currentTarget, path);
            }}
          >
            {title}
          </button>
        ) : (
          <span class={titleClass} data-tool-title="">
            {title}
          </span>
        )}
        {detail !== null && (
          <span class={`tool-row-detail${isMono ? " tool-row-mono" : ""}`} title={detail}>
            {detail}
          </span>
        )}
      </div>
      {canReviewMcpConnection(tool, completion) && (
        <McpReviewAction requestId={tool.requestId} callId={tool.callId} />
      )}
      {(status !== "completed" || showBackground || timeText !== null) && (
        <span class="tool-row-trailing">
          {status !== "completed" && (
            <span class={`tool-row-status${failed ? " is-failed" : ""}`}>
              {statusLabels[status]}
            </span>
          )}
          {showBackground && (
            <span
              class="tool-row-background"
              role="img"
              aria-label="Background call"
              title="Background call"
            >
              <Icon name="background" />
            </span>
          )}
          {timeText !== null && (
            <span class="tool-row-time" title={`Duration: ${timeText}`}>
              {timeText}
            </span>
          )}
        </span>
      )}
    </div>
  );
}

function McpReviewAction({ requestId, callId }: { requestId: string; callId: string }) {
  const { reviewMcpConnection } = useToolRowInteractions();
  const controller = useRef<AbortController | null>(null);
  const [state, setState] = useState<"idle" | "loading" | "error">("idle");
  useEffect(() => () => controller.current?.abort(), [requestId, callId]);

  async function review(anchor: HTMLButtonElement) {
    if (controller.current) return;
    const current = new AbortController();
    controller.current = current;
    setState("loading");
    try {
      await reviewMcpConnection(anchor, requestId, callId, current.signal);
      if (!current.signal.aborted) setState("idle");
    } catch {
      if (!current.signal.aborted) setState("error");
    } finally {
      if (controller.current === current) controller.current = null;
    }
  }

  return (
    <button
      type="button"
      class="tool-row-review"
      disabled={state === "loading"}
      title={state === "error" ? "Could not load connection proposal" : "Review MCP connection"}
      onClick={(event) => void review(event.currentTarget)}
    >
      {state === "loading" ? "Loading…" : state === "error" ? "Retry review" : "Review connection"}
    </button>
  );
}
