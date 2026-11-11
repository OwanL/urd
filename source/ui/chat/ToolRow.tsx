import { useEffect, useRef, useState } from "preact/hooks";
import type { TranscriptToolFacts, ToolCallLabel } from "../sessions/Messages.ts";
import { Icon } from "../components/Icon.tsx";
import type { IconName } from "../components/Icon.tsx";
import {
  formatElapsedTime,
  isToolCallRunning,
  toolElapsedMilliseconds,
  toolRowTimeLabel,
} from "../app/ToolRowPresentation.ts";
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
  write: "file-write",
  edit: "edit",
  listPending: "list",
  cancelPending: "cancel",
  generic: "tool",
};

const iconLabels: Record<ToolCallLabel["icon"], string> = {
  terminal: "Terminal",
  read: "Read file",
  write: "Write file",
  edit: "Edit file",
  listPending: "List pending calls",
  cancelPending: "Cancel pending call",
  generic: "Tool",
};

function ToolRowIcon({
  name,
  label,
  running,
  failed,
}: {
  name: IconName;
  label: string;
  running: boolean;
  failed: boolean;
}) {
  return (
    <span
      class={`tool-row-icon${running ? " is-running" : ""}${failed ? " is-failed" : ""}`}
      role="img"
      aria-label={failed ? `Failed: ${label}` : label}
      data-tool-icon={name}
    >
      <Icon name={name} />
    </span>
  );
}

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
  const [intersecting, setIntersecting] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const running = isToolCallRunning(tool, completion);
  const failed = tool.outcome === "failed";
  const iconName = completion ? "return" : label ? iconNames[label.icon] : "tool";
  const iconLabel = completion
    ? "Returned tool output"
    : label
      ? iconLabels[label.icon]
      : (tool.name ?? "Tool");
  const title = label?.title ?? tool.name ?? "Tool";
  const detail = label?.detail ?? null;
  const isMono = label?.icon === "terminal" || label?.icon === "generic";
  const measuredElapsed = toolElapsedMilliseconds(tool, now, completion);
  const timeText = measuredElapsed === null ? null : formatElapsedTime(measuredElapsed);
  const timeLabel = toolRowTimeLabel(tool, timeText, completion);
  const batchedWithPrevious = tool.batchedWithPrevious;
  const batchedWithNext = tool.batchedWithNext;
  const rowClass = [
    "tool-row",
    batchedWithPrevious || batchedWithNext ? "is-batched" : "",
    completion ? "is-completion" : "",
  ]
    .filter(Boolean)
    .join(" ");

  useEffect(() => {
    const node = row.current;
    if (!node || !root) return;
    if (typeof IntersectionObserver === "undefined") {
      setIntersecting(true);
      return;
    }
    const observer = new IntersectionObserver(
      ([intersection]) => setIntersecting(intersection?.isIntersecting ?? false),
      { root },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [root]);

  useEffect(() => {
    if (!running || paused || !intersecting) return;
    setNow(Date.now());
    return subscribeToToolRowClock(setNow);
  }, [intersecting, paused, running]);

  return (
    <div
      ref={row}
      class={rowClass}
      data-tool-row-kind={completion ? "completion" : "call"}
      data-tool-name={tool.name ?? ""}
      data-tool-request-id={tool.requestId}
      data-tool-call-id={tool.callId}
      data-tool-outcome={tool.outcome ?? ""}
      data-tool-background={tool.background}
      data-batched-with-previous={batchedWithPrevious}
      data-batched-with-next={batchedWithNext}
    >
      <ToolRowIcon name={iconName} label={iconLabel} running={running} failed={failed} />
      <span
        class={`tool-row-title${isMono ? " tool-row-mono" : ""}${completion ? " is-muted" : ""}`}
        title={label?.path ?? undefined}
        data-tool-title=""
        data-tool-path={label?.path ?? undefined}
      >
        {title}
      </span>
      {detail !== null && (
        <span class={`tool-row-detail${isMono ? " tool-row-mono" : ""}`}>{detail}</span>
      )}
      {(timeText !== null || (tool.background && !completion)) && (
        <span class={`tool-row-time${running ? " is-live" : ""}`}>
          {tool.background && !completion && (
            <span
              class="tool-row-background"
              role="img"
              aria-label={timeLabel ?? "Background call"}
            >
              <Icon name="background" />
            </span>
          )}
          {timeText !== null && (
            <span aria-hidden={tool.background && !completion}>{timeText}</span>
          )}
        </span>
      )}
    </div>
  );
}
