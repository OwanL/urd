import { useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { moveTab } from "./TabOrder.ts";
import { useTabDrag } from "./useTabDrag.ts";
import "./chat-tabs.css";

export interface ChatTabsProps {
  tabs: readonly { id: string; title: string }[];
  activeId: string | null;
  onOpen(id: string): void;
  onClose(id: string): void;
  onReorder(id: string, beforeId: string | null): void;
}

export function ChatTabs({ tabs, activeId, onOpen, onClose, onReorder }: ChatTabsProps) {
  const [announcement, setAnnouncement] = useState("");
  const drag = useTabDrag(tabs, (id, beforeId) => {
    reorder(id, beforeId);
    focusTab(id);
  });

  function focusTab(id: string) {
    requestAnimationFrame(() => {
      const tab = document.getElementById(`tab-${id}`);
      tab?.focus({ preventScroll: true });
      tab?.closest(".chat-tab")?.scrollIntoView({ block: "nearest", inline: "nearest" });
    });
  }
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
      {tabs.map(({ id, title }) => (
        <div
          key={id}
          data-tab-id={id}
          class={`chat-tab ${id === activeId ? "is-active" : ""} ${drag.draggedId === id ? "is-dragging" : ""}`}
        >
          <Tooltip
            disabled={drag.draggedId !== null}
            placement="below"
            content={
              <>
                {title}
                <span class="tooltip-detail">
                  Move tab <kbd>Alt</kbd> + <kbd>Shift</kbd> + <kbd>← / →</kbd>
                </span>
              </>
            }
          >
            <button
              id={`tab-${id}`}
              role="tab"
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
                  onClose(id);
                }
              }}
            >
              {title}
            </button>
          </Tooltip>
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
            onClick={() => onClose(id)}
          />
        </div>
      ))}
      <span class="tab-order-announcement" aria-live="polite" aria-atomic="true">
        {announcement}
      </span>
    </div>
  );
}
