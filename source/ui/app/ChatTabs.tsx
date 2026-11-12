import type { RefObject } from "preact";
import { useLayoutEffect, useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { isBusySessionStatus } from "./SessionActivity.ts";
import { moveTab } from "./TabOrder.ts";
import { useTabDrag } from "./useTabDrag.ts";
import "./chat-tabs.css";

const statusLabels: Record<string, string> = {
  running: "Running",
  waiting: "Waiting for tools",
  compacting: "Compacting",
  idle: "Idle",
  failed: "Failed",
  fenced: "Fenced",
  "needs-attention": "Needs attention",
  "updates-paused": "Updates paused",
};

export interface ChatTabsProps {
  tabs: readonly {
    id: string;
    title: string;
    status?: string | undefined;
    knownStatus?: string | undefined;
  }[];
  activeId: string | null;
  unread: ReadonlySet<string>;
  emptyFocusTarget: RefObject<HTMLButtonElement | null>;
  onOpen(id: string): void;
  onClose(id: string): void;
  onReorder(id: string, beforeId: string | null): void;
}

export function ChatTabs({
  tabs,
  activeId,
  unread,
  emptyFocusTarget,
  onOpen,
  onClose,
  onReorder,
}: ChatTabsProps) {
  const [announcement, setAnnouncement] = useState("");
  const drag = useTabDrag(tabs, (id, beforeId) => {
    reorder(id, beforeId);
    focusTab(id);
  });

  function focusTab(id: string) {
    requestAnimationFrame(() => {
      const tab = drag.strip.current?.querySelector<HTMLButtonElement>(`#tab-${CSS.escape(id)}`);
      tab?.focus({ preventScroll: true });
      tab?.closest(".chat-tab")?.scrollIntoView({ block: "nearest", inline: "nearest" });
    });
  }
  function closeTab(id: string) {
    const tab = tabs.find((item) => item.id === id);
    if (
      isBusySessionStatus(tab?.status) ||
      (tab?.status === "updates-paused" && isBusySessionStatus(tab.knownStatus))
    )
      return;
    onClose(id);
    requestAnimationFrame(() => {
      const selected = drag.strip.current?.querySelector<HTMLButtonElement>(
        '[role="tab"][aria-selected="true"]',
      );
      (selected ?? emptyFocusTarget.current)?.focus();
    });
  }
  const activeTitle = tabs.find((tab) => tab.id === activeId)?.title;
  useLayoutEffect(() => {
    if (activeId === null) return;
    drag.strip.current
      ?.querySelector(`#tab-${CSS.escape(activeId)}`)
      ?.closest(".chat-tab")
      ?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [activeId, activeTitle, drag.strip]);

  function reorder(id: string, beforeId: string | null) {
    const next = moveTab(tabs, id, beforeId);
    if (next === tabs) return;
    const index = next.findIndex((tab) => tab.id === id);
    setAnnouncement(`${next[index]!.title}, position ${index + 1} of ${next.length}`);
    onReorder(id, beforeId);
  }

  return (
    <div
      ref={drag.strip}
      class="chat-tabs"
      role="tablist"
      aria-label="Chats"
      onPointerDownCapture={drag.resetClick}
      onClickCapture={drag.suppressClick}
      onKeyDown={(event) => {
        if (drag.draggedId) return;
        const focused = (event.target as HTMLElement).closest<HTMLElement>(".chat-tab")?.dataset
          .tabId;
        const index = tabs.findIndex((tab) => tab.id === (focused ?? activeId));
        if (event.altKey && event.shiftKey && ["ArrowLeft", "ArrowRight"].includes(event.key)) {
          event.preventDefault();
          const from = tabs.findIndex((tab) => tab.id === focused);
          const to = from + (event.key === "ArrowLeft" ? -1 : 1);
          if (from >= 0 && to >= 0 && to < tabs.length) {
            reorder(
              focused!,
              event.key === "ArrowLeft" ? tabs[to]!.id : (tabs[to + 1]?.id ?? null),
            );
            focusTab(focused!);
          }
          return;
        }
        if (!tabs.length || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        const next =
          event.key === "Home"
            ? 0
            : event.key === "End"
              ? tabs.length - 1
              : (index + (event.key === "ArrowLeft" ? -1 : 1) + tabs.length) % tabs.length;
        const id = tabs[next]!.id;
        onOpen(id);
        focusTab(id);
      }}
    >
      {tabs.map(({ id, title, status, knownStatus }) => {
        const busy = isBusySessionStatus(status);
        const pausedBusy = status === "updates-paused" && isBusySessionStatus(knownStatus);
        const completed = unread.has(id) && id !== activeId && status === "idle";
        const lastKnownLabel = knownStatus ? (statusLabels[knownStatus] ?? knownStatus) : null;
        const statusLabel = completed
          ? "Completed"
          : status === "updates-paused" && lastKnownLabel
            ? `Updates paused; last observed ${lastKnownLabel}`
            : status
              ? (statusLabels[status] ?? status)
              : null;
        const indicatorStatus =
          status === "updates-paused" &&
          ["failed", "fenced", "needs-attention"].includes(knownStatus ?? "")
            ? knownStatus
            : status;
        const indicator =
          indicatorStatus === "failed"
            ? "!"
            : indicatorStatus === "fenced"
              ? "⊘"
              : indicatorStatus === "needs-attention"
                ? "?"
                : indicatorStatus === "updates-paused" && !pausedBusy
                  ? "Ⅱ"
                  : indicatorStatus && !busy && indicatorStatus !== "idle"
                    ? "?"
                    : null;
        return (
          <div
            key={id}
            data-tab-id={id}
            class={`chat-tab ${id === activeId ? "is-active" : ""} ${drag.draggedId === id ? "is-dragging" : ""} ${completed ? "has-unread" : ""}`}
          >
            <Tooltip
              disabled={drag.draggedId !== null}
              placement="below"
              content={
                <>
                  {title}
                  {statusLabel && <span class="tooltip-detail">{statusLabel}</span>}
                  <span class="tooltip-detail">
                    Move tab <kbd>Alt</kbd> + <kbd>Shift</kbd> + <kbd>← / →</kbd>
                  </span>
                </>
              }
            >
              <button
                id={`tab-${id}`}
                role="tab"
                aria-label={`${title}${statusLabel ? `, ${statusLabel}` : ""}`}
                aria-selected={id === activeId}
                aria-controls="chat-view"
                aria-keyshortcuts={
                  "Alt+Shift+ArrowLeft Alt+Shift+ArrowRight" // gitleaks:allow -- public keyboard shortcuts, not a credential
                }
                tabIndex={id === activeId ? 0 : -1}
                onPointerDown={(event) => drag.start(event, id)}
                onDragStart={(event) => event.preventDefault()}
                onClick={() => onOpen(id)}
                onKeyDown={(event) => {
                  if (event.key === "Delete" && !drag.draggedId) {
                    event.preventDefault();
                    closeTab(id);
                  }
                }}
              >
                <span class="tab-title">{title}</span>
                {indicator && (
                  <span
                    class={`tab-status status-${indicatorStatus}`}
                    role="img"
                    aria-label={statusLabel ?? status}
                  >
                    {indicator}
                  </span>
                )}
              </button>
            </Tooltip>
            <div class="tab-end-slot">
              {busy ? (
                <Tooltip
                  placement="below"
                  content={
                    <>
                      {title}
                      {statusLabel && <span class="tooltip-detail">{statusLabel}</span>}
                    </>
                  }
                >
                  <span class="tab-activity-slot" role="img" aria-label={statusLabel ?? status}>
                    <span class="tab-activity-dot is-busy" />
                  </span>
                </Tooltip>
              ) : pausedBusy ? (
                <Tooltip
                  placement="below"
                  content={
                    <>
                      {title}
                      <span class="tooltip-detail">{statusLabel}</span>
                    </>
                  }
                >
                  <span
                    class="tab-activity-slot tab-paused-slot"
                    role="img"
                    aria-label={statusLabel!}
                  >
                    Ⅱ
                  </span>
                </Tooltip>
              ) : (
                <>
                  {completed && (
                    <Tooltip
                      placement="below"
                      content={
                        <>
                          {title}
                          <span class="tooltip-detail">Completed</span>
                        </>
                      }
                    >
                      <span
                        class="tab-activity-slot tab-completion-marker"
                        role="img"
                        aria-label="Completed"
                      >
                        <span class="tab-activity-dot" />
                      </span>
                    </Tooltip>
                  )}
                  <IconButton
                    icon="close"
                    class="close-tab"
                    label={`Close ${title}`}
                    hint={
                      <>
                        Close tab <kbd>Delete</kbd>
                      </>
                    }
                    placement="below"
                    onClick={() => closeTab(id)}
                  />
                </>
              )}
            </div>
          </div>
        );
      })}
      <span class="tab-order-announcement" aria-live="polite" aria-atomic="true">
        {announcement}
      </span>
    </div>
  );
}
