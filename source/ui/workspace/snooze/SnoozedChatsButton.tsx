import { useRef, useState } from "preact/hooks";
import { IconButton } from "../../controls/IconButton.tsx";
import { Popover } from "../../controls/Popover.tsx";
import type { SnoozedChat } from "./SnoozeClient.ts";
import { returnTime } from "./SnoozeDuration.ts";
import "./snooze.css";

export function SnoozedChatsButton({
  chats,
  title,
  enabled,
  connected,
  ready,
  error,
  restore,
  retry,
}: {
  chats: readonly SnoozedChat[];
  title: (chat: SnoozedChat) => string;
  enabled: boolean;
  connected: boolean;
  ready: boolean;
  error: boolean;
  restore: (chat: SnoozedChat) => Promise<string | null>;
  retry: () => void;
}) {
  const anchor = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [restoring, setRestoring] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const busy = useRef(false);

  async function restoreChat(chat: SnoozedChat, button: HTMLButtonElement) {
    if (busy.current) return;
    busy.current = true;
    setRestoring(chat.sessionId);
    setFailure(null);
    const result = await restore(chat);
    if (!result && document.activeElement === button)
      anchor.current?.focus({ preventScroll: true });
    setFailure(result);
    setRestoring(null);
    busy.current = false;
  }

  return (
    <>
      <IconButton
        buttonRef={anchor}
        icon="moon"
        label="Snoozed chats"
        placement="below"
        aria-haspopup="dialog"
        aria-expanded={open}
        disabled={!enabled}
        onClick={() => {
          if (!open) setFailure(null);
          setOpen(!open);
        }}
      />
      {open && (
        <Popover
          anchor={anchor}
          label="Snoozed chats"
          class="snoozed-chats"
          onClose={() => setOpen(false)}
        >
          {!error && !chats.length && (
            <span class="snoozed-empty">{ready ? "No snoozed chats" : "Loading…"}</span>
          )}
          {chats.map((chat) => (
            <div class="snoozed-chat" key={chat.sessionId}>
              <div>
                <span>{title(chat)}</span>
                <time dateTime={chat.until}>{returnTime(new Date(chat.until))}</time>
              </div>
              <button
                type="button"
                aria-label={`Restore now: ${title(chat)}`}
                disabled={!connected}
                aria-disabled={restoring !== null}
                onClick={(event) => void restoreChat(chat, event.currentTarget)}
              >
                {restoring === chat.sessionId ? "Restoring…" : "Restore now"}
              </button>
            </div>
          ))}
          {error && (
            <div class="snooze-error" role="alert">
              Couldn’t load snoozed chats.{" "}
              <button type="button" onClick={retry}>
                Retry
              </button>
            </div>
          )}
          {failure && (
            <div class="snooze-error" role="alert">
              {failure}
            </div>
          )}
        </Popover>
      )}
    </>
  );
}
