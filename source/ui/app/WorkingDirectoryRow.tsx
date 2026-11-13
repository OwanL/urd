import type { ComponentChildren } from "preact";
import { useRef, useState } from "preact/hooks";
import { FolderPicker } from "../components/FolderPicker.tsx";
import { Icon } from "../components/Icon.tsx";

interface WorkingDirectoryRowProps {
  label: string;
  title: string;
  directory: string | null;
  disabled: boolean;
  busy: boolean;
  feedback?: ComponentChildren;
  onSave(path: string): Promise<boolean>;
}

export function WorkingDirectoryRow({
  label,
  title,
  directory,
  disabled,
  busy,
  feedback,
  onSave,
}: WorkingDirectoryRowProps) {
  const anchor = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);

  return (
    <div class="chat-working-directory-row">
      <span class="chat-working-directory-label">{label}</span>
      <code class="chat-working-directory-path" title={directory ?? undefined}>
        {directory ?? "…"}
      </code>
      <button
        ref={anchor}
        class="chat-working-directory-edit"
        type="button"
        aria-label={`Edit ${label.toLowerCase()}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-disabled={disabled || busy}
        onClick={() => {
          if (!disabled && !busy) setOpen(true);
        }}
      >
        <Icon name="edit" />
        Edit
      </button>
      {open ? (
        <FolderPicker
          anchor={anchor}
          title={title}
          initialPath={directory}
          busy={busy}
          feedback={feedback}
          onChoose={onSave}
          onClose={(returnFocus) => {
            setOpen(false);
            if (returnFocus) anchor.current?.focus({ preventScroll: true });
          }}
        />
      ) : feedback ? (
        <div class="chat-working-directory-feedback">{feedback}</div>
      ) : null}
    </div>
  );
}
