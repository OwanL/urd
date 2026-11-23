import type { RefObject } from "preact";
import { useRef, useState } from "preact/hooks";
import { Icon } from "../../controls/Icon.tsx";
import { Popover } from "../../controls/Popover.tsx";
import { SnoozePicker } from "../snooze/SnoozePicker.tsx";

export function TabContextMenu({
  anchor,
  title,
  canSnooze,
  canClose,
  close,
  snooze,
  dismiss,
  onSnoozed,
}: {
  anchor: RefObject<HTMLElement | null>;
  title: string;
  canSnooze: boolean;
  canClose: boolean;
  close: () => void;
  snooze: (until: string) => Promise<string | null>;
  dismiss: () => void;
  onSnoozed: () => void;
}) {
  const [pickingDuration, setPickingDuration] = useState(false);
  const snoozeItem = useRef<HTMLButtonElement>(null);
  const closeItem = useRef<HTMLButtonElement>(null);
  if (pickingDuration) {
    return (
      <SnoozePicker
        anchor={anchor}
        title={title}
        available={canSnooze}
        snooze={snooze}
        onClose={dismiss}
        onSnoozed={onSnoozed}
      />
    );
  }

  return (
    <Popover
      anchor={anchor}
      label={`${title} actions`}
      role="menu"
      class="chat-tab-menu"
      {...(canSnooze || canClose ? { initialFocus: canSnooze ? snoozeItem : closeItem } : {})}
      onClose={dismiss}
    >
      <div
        role="presentation"
        onKeyDown={(event) => {
          const enabled = [snoozeItem.current, closeItem.current].filter(
            (item) => item && !item.disabled,
          );
          if (!enabled.length || !["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key))
            return;
          event.preventDefault();
          const index = enabled.findIndex((item) => item === document.activeElement);
          const step = event.key === "ArrowDown" ? 1 : -1;
          const next =
            event.key === "Home"
              ? 0
              : event.key === "End"
                ? enabled.length - 1
                : (index + step + enabled.length) % enabled.length;
          enabled[next]?.focus();
        }}
      >
        <button
          type="button"
          role="menuitem"
          ref={snoozeItem}
          disabled={!canSnooze}
          onClick={() => setPickingDuration(true)}
        >
          <Icon name="moon" /> Snooze…
        </button>
        <button type="button" role="menuitem" ref={closeItem} disabled={!canClose} onClick={close}>
          <Icon name="close" /> Close chat
        </button>
      </div>
    </Popover>
  );
}
