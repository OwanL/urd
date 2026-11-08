import { useEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { moveTab, tabInsertion } from "./TabOrder.ts";
import "./chat-tabs.css";

export interface ChatTabsProps {
  tabs: readonly { id: string; title: string }[];
  activeId: string | null;
  onOpen(id: string): void;
  onClose(id: string): void;
  onReorder(id: string, beforeId: string | null): void;
}

export function ChatTabs({ tabs, activeId, onOpen, onClose, onReorder }: ChatTabsProps) {
  const strip = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: string; order: typeof tabs } | null>(null);
  const suppressClick = useRef(false);
  const frame = useRef(0);
  const [preview, setPreview] = useState<{ id: string; beforeId: string | null } | null>(null);
  const [announcement, setAnnouncement] = useState("");
  useEffect(() => () => cancelAnimationFrame(frame.current), []);

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
  function insertion(x: number) {
    const positions = [...strip.current!.querySelectorAll<HTMLElement>(".chat-tab")]
      .filter((element) => element.dataset.tabId !== drag.current?.id)
      .map((element) => {
        const { left, right } = element.getBoundingClientRect();
        return { id: element.dataset.tabId!, left, right };
      });
    return tabInsertion(positions, x);
  }
  function finish() {
    cancelAnimationFrame(frame.current);
    drag.current = null;
    setPreview(null);
  }
  function showInsertion(x: number) {
    const item = drag.current;
    if (!item) return;
    const beforeId = insertion(x);
    setPreview((previous) =>
      previous?.id === item.id && previous.beforeId === beforeId
        ? previous
        : { id: item.id, beforeId },
    );
  }
  function scrollEdge(x: number) {
    cancelAnimationFrame(frame.current);
    const element = strip.current!;
    const { left, right, width } = element.getBoundingClientRect();
    const edge = Math.min(32, width / 4);
    const speed = x < left + edge ? -8 : x > right - edge ? 8 : 0;
    function tick() {
      element.scrollLeft += speed;
      showInsertion(x);
      if (speed && drag.current) frame.current = requestAnimationFrame(tick);
    }
    tick();
  }

  return (
    <div
      ref={strip}
      class="chat-tabs"
      role="tablist"
      aria-label="Chats"
      onPointerDownCapture={() => {
        suppressClick.current = false;
      }}
      onClickCapture={(event) => {
        if (!suppressClick.current || event.detail === 0) return;
        event.preventDefault();
        event.stopPropagation();
      }}
      onDragOver={(event) => {
        if (!drag.current) return;
        event.preventDefault();
        event.dataTransfer!.dropEffect = "move";
        scrollEdge(event.clientX);
      }}
      onDragLeave={(event) => {
        if (
          event.relatedTarget instanceof Node &&
          event.currentTarget.contains(event.relatedTarget)
        )
          return;
        cancelAnimationFrame(frame.current);
        setPreview(null);
      }}
      onDrop={(event) => {
        const item = drag.current;
        if (!item) return;
        event.preventDefault();
        if (
          tabs.length === item.order.length &&
          tabs.every((tab, index) => tab.id === item.order[index]?.id)
        )
          reorder(item.id, insertion(event.clientX));
        finish();
        focusTab(item.id);
      }}
      onDragEnd={finish}
      onKeyDown={(event) => {
        if (drag.current) return;
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
      {tabs.map(({ id, title }, index) => (
        <div
          key={id}
          data-tab-id={id}
          class={`chat-tab ${id === activeId ? "is-active" : ""} ${preview?.id === id ? "is-dragging" : ""} ${preview?.beforeId === id ? "reorder-before" : ""} ${preview && preview.beforeId === null && index === tabs.length - 1 ? "reorder-end" : ""}`}
        >
          <Tooltip
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
              draggable
              onDragStart={(event) => {
                drag.current = { id, order: tabs };
                suppressClick.current = true;
                event.dataTransfer!.effectAllowed = "move";
                event.dataTransfer!.setData("application/x-urd-tab", id);
              }}
              onClick={() => onOpen(id)}
              onKeyDown={(event) => {
                if (event.key === "Delete") {
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
