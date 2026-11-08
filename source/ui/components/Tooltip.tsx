import { cloneElement } from "preact";
import type { ComponentChildren, VNode } from "preact";
import { useCallback, useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Popover } from "./Popover.tsx";
import type { PopoverPlacement } from "./PopoverGeometry.ts";

export interface TooltipProps {
  content: ComponentChildren;
  children: VNode;
  disabled?: boolean;
  placement?: PopoverPlacement;
}

// Descriptive formatted content only (including kbd). Use Popover for interactive content.
// The single child must be a control, or forward aria-describedby to its actual control.
export function Tooltip({
  content,
  children,
  disabled = false,
  placement = "above",
}: TooltipProps) {
  const anchor = useRef<HTMLElement>(null);
  const id = useId();
  const [open, setOpen] = useState(false);
  const enterTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const exitTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const suppressed = useRef(false);
  const focused = useRef(false);
  const clearTimers = useCallback(() => {
    clearTimeout(enterTimer.current);
    clearTimeout(exitTimer.current);
    enterTimer.current = undefined;
    exitTimer.current = undefined;
  }, []);
  const dismiss = useCallback(() => {
    clearTimers();
    suppressed.current = true;
    setOpen(false);
  }, [clearTimers]);

  useLayoutEffect(() => {
    if (disabled) dismiss();
    document.addEventListener("urd:popover-open", dismiss);
    document.addEventListener("pointerdown", dismiss, true);
    return () => {
      clearTimers();
      document.removeEventListener("urd:popover-open", dismiss);
      document.removeEventListener("pointerdown", dismiss, true);
    };
  }, [disabled, dismiss, clearTimers]);

  const visible = open && !disabled;
  const control = children as VNode<{ "aria-describedby"?: string }>;
  const description = [control.props["aria-describedby"], visible ? id : undefined]
    .filter(Boolean)
    .join(" ");

  return (
    <span
      ref={(element) => {
        anchor.current = (element?.firstElementChild as HTMLElement | null) ?? null;
      }}
      class="tooltip-anchor"
      onPointerEnter={(event) => {
        if (event.pointerType === "touch" || disabled) return;
        clearTimeout(exitTimer.current);
        suppressed.current = false;
        clearTimeout(enterTimer.current);
        enterTimer.current = setTimeout(() => setOpen(true), 350);
      }}
      onPointerLeave={() => {
        clearTimers();
        suppressed.current = false;
        if (!focused.current) exitTimer.current = setTimeout(() => setOpen(false), 140);
      }}
      onFocusIn={(event) => {
        focused.current = true;
        clearTimers();
        // A fresh keyboard interaction must work after Escape, clicks, or an earlier blur.
        if (event.target instanceof Element && event.target.matches(":focus-visible"))
          suppressed.current = false;
        if (!disabled && !suppressed.current) setOpen(true);
      }}
      onFocusOut={() => {
        focused.current = false;
        dismiss();
        suppressed.current = false;
      }}
      onClick={dismiss}
    >
      {cloneElement(control, description ? { "aria-describedby": description } : {})}
      {visible && (
        <Popover anchor={anchor} id={id} role="tooltip" placement={placement} onClose={dismiss}>
          {content}
        </Popover>
      )}
    </span>
  );
}
