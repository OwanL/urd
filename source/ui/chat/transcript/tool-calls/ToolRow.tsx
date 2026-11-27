import { useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import type { TranscriptToolFacts, ToolCallLabel } from "../../../sessions/transcript/Messages.ts";
import type { ToolDraft } from "../../../sessions/SessionEvents.ts";
import { Icon, type IconName } from "../../../controls/Icon.tsx";
import { useToolRowInteractions } from "./ToolRowInteractions.tsx";
import type { presentToolDraft } from "./ToolDraftPresentation.ts";
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

const statusLabels: Record<ToolRowStatus | "drafted" | "drafting", string> = {
  drafted: "Drafted",
  drafting: "Drafting",
  completed: "Completed",
  failed: "Failed",
  cancelled: "Cancelled",
  interrupted: "Interrupted",
  unknown: "Unknown",
  "not-run": "Not run",
  pending: "Pending",
  running: "Running",
};

interface DraftToolRowProps {
  draft: ToolDraft;
  presentation: ReturnType<typeof presentToolDraft>;
  batchedWithPrevious: boolean;
  batchedWithNext: boolean;
}

export function ToolRow({
  tool,
  label,
  draftRow,
  completion = false,
  root,
  paused,
  motion = false,
  enter = false,
}: {
  root: HTMLElement | null;
  motion?: boolean;
  enter?: boolean;
  paused: boolean;
  completion?: boolean;
} & (
  | { tool: TranscriptToolFacts; label: ToolCallLabel | null; draftRow?: never }
  | { draftRow: DraftToolRowProps; tool?: never; label?: never }
)) {
  const row = useRef<HTMLDivElement>(null);
  const icon = useRef<HTMLButtonElement>(null);
  const titleNode = useRef<HTMLButtonElement>(null);
  const interactions = useToolRowInteractions();
  const [now, setNow] = useState(() => Date.now());
  const status = tool
    ? toolRowStatus(tool, completion)
    : draftRow?.draft.state === "drafting"
      ? "drafting"
      : "drafted";
  const batchedWithPrevious = draftRow?.batchedWithPrevious ?? tool?.batchedWithPrevious ?? false;
  const next = draftRow?.batchedWithNext ?? tool?.batchedWithNext ?? false;
  const previous = useRef({ status, next });
  const [transition, setTransition] = useState(false);
  const [flash, setFlash] = useState(false);
  const [rail, setRail] = useState(() => motion && enter && batchedWithPrevious);
  useLayoutEffect(() => {
    const old = previous.current;
    previous.current = { status, next };
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    if (motion && !old.next && next) setRail(true);
    if ((old.status === "drafted" || old.status === "pending") && status === "running")
      setTransition(true);
    if (old.status === "running" && status !== "running" && status !== "pending") setFlash(true);
  }, [status, next, motion]);
  useEffect(() => {
    if (!transition) return;
    const timer = window.setTimeout(() => setTransition(false), 100);
    return () => window.clearTimeout(timer);
  }, [transition]);
  useEffect(() => {
    if (!flash && !rail) return;
    const timer = window.setTimeout(() => {
      setFlash(false);
      setRail(false);
    }, 400);
    return () => window.clearTimeout(timer);
  }, [flash, rail]);
  const running = status === "running";
  const failed = status === "failed";
  const payloadFinished = Boolean(tool?.isClosed && tool.outcome !== null);
  const sourceChanged = interactions.sourceChanged;
  useEffect(() => {
    if (icon.current) sourceChanged(icon.current, payloadFinished);
  }, [payloadFinished, sourceChanged, tool?.outcome, tool?.isClosed, tool?.endedAt]);

  const sourceUnmounted = interactions.sourceUnmounted;
  const durable = tool !== undefined;
  useEffect(() => {
    const source = icon.current;
    if (!durable || !source) return;
    return () => sourceUnmounted(source);
  }, [durable, sourceUnmounted]);

  const path = label?.path;
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

  const presentation = draftRow?.presentation ?? label;
  const kind = presentation?.icon;
  const isAskUser = tool?.name === "ask_user";
  const iconName = completion ? "return" : isAskUser ? "ask" : kind ? iconNames[kind] : "tool";
  const iconLabel = completion
    ? "Returned tool output"
    : isAskUser
      ? "Question"
      : kind
        ? iconLabels[kind]
        : (tool?.name ?? "Tool");
  const glyph = <Icon name={iconName} />;
  const title = isAskUser ? "Question" : (presentation?.title ?? tool?.name ?? "Tool");
  const detail = isAskUser ? null : (presentation?.detail ?? null);
  const mono = !isAskUser && (kind === "terminal" || kind === "generic") ? " tool-row-mono" : "";
  const titleClass = `tool-row-title${mono}`;
  const measuredElapsed = tool ? toolElapsedMilliseconds(tool, now, completion) : null;
  const timeText = draftRow
    ? `${draftRow.draft.argumentBytes.toLocaleString()} B`
    : measuredElapsed === null
      ? null
      : formatElapsedTime(measuredElapsed);
  const showBackground = tool?.background && !completion;
  const rowClass = [
    "tool-row is-call-row",
    draftRow ? "is-draft" : "",
    status === "drafting" ? "is-drafting" : "",
    batchedWithPrevious || next ? "is-batched" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div
      ref={row}
      class={rowClass}
      data-rail-draw={rail}
      data-tool-row-kind={draftRow ? "draft" : completion ? "completion" : "call"}
      data-tool-name={tool && (tool.name ?? "")}
      data-tool-request-id={tool?.requestId}
      data-tool-call-id={tool?.callId}
      data-tool-outcome={tool && (tool.outcome ?? "")}
      data-tool-status={tool && status}
      data-tool-background={tool?.background}
      data-batched-with-previous={batchedWithPrevious}
      data-batched-with-next={next}
    >
      {rail && batchedWithPrevious && next && (
        <span class="tool-row-rail-extension" aria-hidden="true" />
      )}
      {tool && flash && <span class="tool-row-flash" aria-hidden="true" />}
      {tool ? (
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
          {glyph}
        </button>
      ) : (
        <span class="tool-row-icon" aria-label={iconLabel} role="img">
          {glyph}
        </span>
      )}
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
          <span class={`tool-row-detail${mono}`} title={detail}>
            {detail}
          </span>
        )}
      </div>
      {(status !== "completed" || showBackground || timeText !== null) && (
        <span class="tool-row-trailing">
          {status !== "completed" && (
            <span
              class={`tool-row-status${failed ? " is-failed" : ""}${transition && running ? " is-transitioning" : ""}`}
            >
              {draftRow ? (
                statusLabels[status]
              ) : (
                <span class="tool-row-status-current">{statusLabels[status]}</span>
              )}
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
            <span class="tool-row-time" title={draftRow ? undefined : `Duration: ${timeText}`}>
              {timeText}
            </span>
          )}
        </span>
      )}
    </div>
  );
}
