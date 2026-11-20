import type { RefObject } from "preact";
import { useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { IconButton } from "../../controls/IconButton.tsx";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { isBusySessionStatus } from "../SessionActivity.ts";
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
  unavailable: "Session unavailable",
  "updates-paused": "Updates paused",
};

export interface ChatTabsProps {
  tabs: readonly {
    id: string;
    title: string;
    status?: string | undefined;
    knownStatus?: string | undefined;
    closeAvailable: boolean;
    closing: boolean;
  }[];
  activeId: string | null;
  unread: ReadonlySet<string>;
  emptyFocusTarget: RefObject<HTMLButtonElement | null>;
  onOpen(id: string): void;
  onClose(id: string): Promise<void>;
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
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  const confirmation = useRef<HTMLDialogElement>(null);
  const confirming = tabs.find((tab) => tab.id === confirmingId);
  useLayoutEffect(() => {
    const dialog = confirmation.current;
    if (confirming && dialog && !dialog.open) dialog.showModal();
    if (!confirming && confirmingId !== null) setConfirmingId(null);
  }, [confirming, confirmingId]);
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
  async function closeTab(id: string) {
    confirmation.current?.close();
    setConfirmingId(null);
    const closingTab = document.getElementById(`tab-${id}`);
    closingTab?.focus({ preventScroll: true });
    await onClose(id);
    requestAnimationFrame(() => {
      if (closingTab?.isConnected || document.activeElement !== document.body) return;
      const selected = drag.strip.current?.querySelector<HTMLButtonElement>(
        '[role="tab"][aria-selected="true"]',
      );
      (selected ?? emptyFocusTarget.current)?.focus({ preventScroll: true });
    });
  }
  const activeTitle = tabs.find((tab) => tab.id === activeId)?.title;
  useLayoutEffect(() => {
    const strip = drag.strip.current;
    if (!strip || activeId === null) return;
    const revealActive = () => {
      if (drag.draggedId !== null) return;
      strip
        .querySelector(`#tab-${CSS.escape(activeId)}`)
        ?.closest(".chat-tab")
        ?.scrollIntoView({ block: "nearest", inline: "nearest" });
    };
    revealActive();
    let width = strip.clientWidth;
    const observer = new ResizeObserver(() => {
      if (strip.clientWidth === width) return;
      width = strip.clientWidth;
      revealActive();
    });
    observer.observe(strip);
    return () => observer.disconnect();
  }, [activeId, activeTitle, drag.strip, drag.draggedId]);

  function reorder(id: string, beforeId: string | null) {
    const next = moveTab(tabs, id, beforeId);
    if (next === tabs) return;
    const index = next.findIndex((tab) => tab.id === id);
    setAnnouncement(`${next[index]!.title}, position ${index + 1} of ${next.length}`);
    onReorder(id, beforeId);
  }

  const tabStrip = (
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
      {tabs.map(({ id, title, status, knownStatus, closeAvailable, closing }) => {
        const busy = isBusySessionStatus(status);
        const pausedBusy = status === "updates-paused" && isBusySessionStatus(knownStatus);
        const completed = unread.has(id) && id !== activeId && status === "idle";
        const lastKnownLabel = knownStatus ? (statusLabels[knownStatus] ?? knownStatus) : null;
        const statusLabel = closing
          ? "Closing…"
          : completed
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
                : indicatorStatus &&
                    !busy &&
                    indicatorStatus !== "idle" &&
                    indicatorStatus !== "updates-paused"
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
                aria-busy={closing}
                aria-controls="chat-view"
                aria-keyshortcuts={
                  closeAvailable
                    ? "Alt+Shift+ArrowLeft Alt+Shift+ArrowRight Delete"
                    : "Alt+Shift+ArrowLeft Alt+Shift+ArrowRight" // gitleaks:allow -- public keyboard shortcuts, not a credential
                }
                tabIndex={id === activeId ? 0 : -1}
                onPointerDown={(event) => drag.start(event, id)}
                onDragStart={(event) => event.preventDefault()}
                onClick={() => onOpen(id)}
                onKeyDown={(event) => {
                  if (event.key === "Delete" && !event.repeat && !drag.draggedId) {
                    event.preventDefault();
                    if (closeAvailable) setConfirmingId(id);
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
                    <span class="tab-activity-dot" />
                  </span>
                </Tooltip>
              ) : pausedBusy ? null : (
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
                        <Icon name="check" />
                      </span>
                    </Tooltip>
                  )}
                  <IconButton
                    icon="close"
                    class="close-tab"
                    label={closing ? "Closing chat…" : `Close ${title}`}
                    disabled={!closeAvailable}
                    hint={
                      closing ? (
                        "Closing chat…"
                      ) : closeAvailable ? (
                        <>
                          Close chat <kbd>Delete</kbd>
                        </>
                      ) : (
                        "Close unavailable"
                      )
                    }
                    placement="below"
                    onClick={() => setConfirmingId(id)}
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

  return (
    <>
      {tabStrip}
      {confirming && (
        <dialog
          ref={confirmation}
          class="close-chat-dialog"
          aria-labelledby="close-chat-title"
          aria-describedby="close-chat-description"
          onClose={() => setConfirmingId(null)}
        >
          <h2 id="close-chat-title">Close “{confirming.title}”?</h2>
          <p id="close-chat-description">
            This permanently deletes the conversation and its draft. It can’t be reopened.
          </p>
          <div class="close-chat-actions">
            <button type="button" autoFocus onClick={() => confirmation.current?.close()}>
              Cancel
            </button>
            <button
              type="button"
              disabled={!confirming.closeAvailable}
              onClick={() => void closeTab(confirming.id)}
            >
              Close permanently
            </button>
          </div>
        </dialog>
      )}
    </>
  );
}
