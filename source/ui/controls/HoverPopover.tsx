import type { ComponentChildren } from "preact";
import { useEffect, useId, useRef, useState } from "preact/hooks";
import { Popover } from "./Popover.tsx";

// Shared hover intent, deliberate activation and nested focus handling for interactive details.
export function HoverPopover({
  label,
  surfaceLabel,
  class: className,
  surfaceClass = "hover-popover",
  enabled = true,
  onReveal,
  children,
  content,
}: {
  label: string;
  surfaceLabel: string;
  class: string;
  surfaceClass?: string;
  enabled?: boolean;
  onReveal?: () => void;
  children: ComponentChildren;
  content: ComponentChildren;
}) {
  const anchor = useRef<HTMLButtonElement>(null);
  const id = useId();
  const [open, setOpen] = useState(false);
  const [pinned, setPinned] = useState(false);
  const enterTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => {
    if (enabled) return;
    clearTimeout(enterTimer.current);
    clearTimeout(closeTimer.current);
    setOpen(false);
    setPinned(false);
  }, [enabled]);
  useEffect(
    () => () => {
      clearTimeout(enterTimer.current);
      clearTimeout(closeTimer.current);
    },
    [],
  );

  function closeSoon() {
    clearTimeout(closeTimer.current);
    if (pinned) return;
    closeTimer.current = setTimeout(() => {
      const focused = document.activeElement;
      if (focused === anchor.current || document.getElementById(id)?.contains(focused)) return;
      setOpen(false);
    }, 220);
  }

  return (
    <>
      <button
        ref={anchor}
        type="button"
        class={className}
        aria-label={label}
        aria-haspopup="dialog"
        aria-controls={id}
        aria-expanded={open}
        onPointerEnter={(event) => {
          if (!enabled || pinned || event.pointerType === "touch") return;
          clearTimeout(closeTimer.current);
          clearTimeout(enterTimer.current);
          enterTimer.current = setTimeout(() => {
            onReveal?.();
            setOpen(true);
          }, 300);
        }}
        onPointerLeave={() => {
          clearTimeout(enterTimer.current);
          closeSoon();
        }}
        onFocus={() => {
          clearTimeout(enterTimer.current);
          clearTimeout(closeTimer.current);
          if (!enabled) return;
          onReveal?.();
          setPinned(false);
          setOpen(true);
        }}
        onClick={() => {
          clearTimeout(enterTimer.current);
          clearTimeout(closeTimer.current);
          if (!enabled) return;
          if (!(open && pinned)) onReveal?.();
          setOpen(!(open && pinned));
          setPinned(!(open && pinned));
        }}
      >
        {children}
      </button>
      {open && enabled && (
        <Popover
          anchor={anchor}
          id={id}
          class={surfaceClass}
          label={surfaceLabel}
          placement="above"
          autoFocus={pinned}
          onClose={() => {
            clearTimeout(enterTimer.current);
            clearTimeout(closeTimer.current);
            setOpen(false);
            setPinned(false);
          }}
        >
          <div
            onPointerEnter={() => clearTimeout(closeTimer.current)}
            onPointerLeave={closeSoon}
            onFocusOut={(event) => {
              if (event.currentTarget.contains(event.relatedTarget as Node | null)) return;
              closeSoon();
            }}
          >
            {content}
          </div>
        </Popover>
      )}
    </>
  );
}
