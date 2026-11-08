import type { ComponentChildren, RefObject } from "preact";
import { useLayoutEffect, useRef } from "preact/hooks";
import { placePopover } from "./PopoverGeometry.ts";
import type { PopoverPlacement } from "./PopoverGeometry.ts";
import "./overlays.css";

export interface PopoverProps {
  anchor: RefObject<HTMLElement | null>;
  id?: string;
  class?: string;
  role?: "dialog" | "listbox" | "tooltip";
  label?: string;
  placement?: PopoverPlacement;
  initialFocus?: RefObject<HTMLElement | null>;
  onClose: (returnFocus: boolean) => void;
  children: ComponentChildren;
}

// Reveal only within the option list, never by scrolling the surrounding conversation.
export function scrollOptionIntoView(option: HTMLElement | null) {
  const list = option?.closest('[role="listbox"]');
  if (!option || !list) return;
  const row = option.getBoundingClientRect();
  const bounds = list.getBoundingClientRect();
  if (row.top < bounds.top) list.scrollTop -= bounds.top - row.top;
  else if (row.bottom > bounds.bottom) list.scrollTop += row.bottom - bounds.bottom;
}

// Mount only while open. The browser top layer avoids clipping without a portal or a modal.
export function Popover({
  anchor,
  id,
  class: className,
  role = "dialog",
  label,
  placement = "below",
  initialFocus,
  onClose,
  children,
}: PopoverProps) {
  const surface = useRef<HTMLDivElement>(null);
  const latest = useRef({ onClose, initialFocus });
  latest.current = { onClose, initialFocus };

  useLayoutEffect(() => {
    const element = surface.current;
    const control = anchor.current;
    if (!element || !control) return;
    let frame = 0;
    let closed = false;
    const interactive = role !== "tooltip";
    const position = () => {
      frame = 0;
      const viewport = { width: window.innerWidth, height: window.innerHeight };
      element.style.maxWidth = `min(var(--popover-max-width, 28rem), ${Math.max(0, viewport.width - 16)}px)`;
      const bounds = element.getBoundingClientRect();
      const height = Math.max(
        bounds.height,
        element.scrollHeight + element.offsetHeight - element.clientHeight,
      );
      const placed = placePopover(
        control.getBoundingClientRect(),
        { width: bounds.width, height },
        viewport,
        placement,
      );
      element.style.left = `${placed.left}px`;
      element.style.top = `${placed.top}px`;
      element.style.maxHeight = `${placed.maxHeight}px`;
      element.dataset.placement = placed.placement;
      element.style.visibility = "visible";
    };
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(position);
    };
    const close = (returnFocus: boolean) => {
      if (closed) return;
      closed = true;
      // Restore synchronously, before a consumer can unmount or replace the anchor.
      if (returnFocus && control.isConnected) control.focus({ preventScroll: true });
      latest.current.onClose(returnFocus);
    };
    const inside = (event: Event) => {
      const path = event.composedPath();
      return path.includes(element) || path.includes(control);
    };
    const outside = (event: Event) => {
      if (!inside(event)) close(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented) return;
      const nearest = event
        .composedPath()
        .find((node) => node instanceof HTMLElement && node.matches(".popover:popover-open"));
      if (interactive && nearest && nearest !== element) return;
      event.preventDefault();
      // Descriptions dismiss before an enclosing menu gets the same Escape.
      if (!interactive) event.stopPropagation();
      close(interactive);
    };

    element.style.visibility = "hidden";
    element.showPopover();
    position();
    if (interactive) {
      document.dispatchEvent(new Event("urd:popover-open"));
      (latest.current.initialFocus?.current ?? element).focus({ preventScroll: true });
    }
    const observer = new ResizeObserver(schedule);
    observer.observe(control);
    observer.observe(element);
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("focusin", outside, true);
    document.addEventListener("keydown", escape, !interactive);
    window.addEventListener("resize", schedule);
    window.addEventListener("scroll", schedule, true);
    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      document.removeEventListener("pointerdown", outside, true);
      document.removeEventListener("focusin", outside, true);
      document.removeEventListener("keydown", escape, !interactive);
      window.removeEventListener("resize", schedule);
      window.removeEventListener("scroll", schedule, true);
      if (element.matches(":popover-open")) element.hidePopover();
    };
  }, [anchor, placement, role]);

  return (
    <div
      ref={surface}
      id={id}
      class={`popover${className ? ` ${className}` : ""}`}
      popover="manual"
      role={role}
      aria-label={label}
      tabIndex={role === "tooltip" ? undefined : -1}
    >
      {children}
    </div>
  );
}
