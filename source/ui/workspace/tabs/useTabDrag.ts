import { useLayoutEffect, useRef, useState } from "preact/hooks";
import { tabDragIndex, tabDragOffset } from "./TabOrder.ts";

// Keep layout fixed during a gesture; transforms use a single content-space snapshot.
export function useTabDrag(
  order: readonly { id: string }[],
  onDrop: (id: string, beforeId: string | null) => void,
) {
  const strip = useRef<HTMLDivElement>(null);
  const latest = useRef({ order, onDrop });
  latest.current = { order, onDrop };
  const stop = useRef<((unmount?: boolean) => void) | null>(null);
  const snapshot = useRef<string[]>([]);
  const suppressClick = useRef(false);
  const [draggedId, setDraggedId] = useState<string | null>(null);
  useLayoutEffect(() => {
    if (stop.current && !sameOrder()) stop.current();
  });
  useLayoutEffect(
    () => () => {
      stop.current?.(true);
    },
    [],
  );

  function sameOrder() {
    return (
      snapshot.current.length === latest.current.order.length &&
      snapshot.current.every((id, index) => id === latest.current.order[index]?.id)
    );
  }

  function start(event: PointerEvent, id: string) {
    if (event.button !== 0 || !event.isPrimary || stop.current) return;
    const element = strip.current!;
    // Transforms can enlarge scrollWidth; never let the dragged tab create scrollable space.
    const maxScroll = Math.max(0, element.scrollWidth - element.clientWidth);
    const control = event.currentTarget as HTMLElement;
    const elements = [...element.querySelectorAll<HTMLElement>(".chat-tab")];
    const positions = elements.map((tab) => {
      const { left, right } = tab.getBoundingClientRect();
      return {
        id: tab.dataset.tabId!,
        left: left + element.scrollLeft,
        right: right + element.scrollLeft,
      };
    });
    snapshot.current = positions.map((tab) => tab.id);
    const from = snapshot.current.indexOf(id);
    if (from < 0) return;
    const originX = event.clientX;
    const originY = event.clientY;
    const grab = originX + element.scrollLeft - positions[from]!.left;
    let x = originX;
    let y = originY;
    let to = from;
    let active = false;
    let frame = 0;
    let lastTime = 0;
    suppressClick.current = false;

    function paint() {
      const bounds = element.getBoundingClientRect();
      const width = positions[from]!.right - positions[from]!.left;
      // Keep the held tab identifiable at an edge while the content scrolls beneath it.
      const left =
        Math.max(bounds.left, Math.min(bounds.right - width, x - grab)) + element.scrollLeft;
      to = tabDragIndex(positions, from, to, x + element.scrollLeft, grab);
      elements.forEach((tab, index) => {
        const offset =
          index === from ? left - positions[from]!.left : tabDragOffset(positions, from, to, index);
        tab.style.transform = `translateX(${offset}px)`;
      });
    }
    function velocity() {
      const bounds = element.getBoundingClientRect();
      if (x < bounds.left || x > bounds.right || y < bounds.top || y > bounds.bottom) return 0;
      const edge = Math.min(32, bounds.width / 4);
      if (x < bounds.left + edge && element.scrollLeft > 0)
        return -480 * (1 - (x - bounds.left) / edge);
      if (x > bounds.right - edge && element.scrollLeft < maxScroll)
        return 480 * (1 - (bounds.right - x) / edge);
      return 0;
    }
    function tick(time: number) {
      frame = 0;
      element.scrollLeft = Math.max(
        0,
        Math.min(
          maxScroll,
          element.scrollLeft + (velocity() * Math.min(time - lastTime, 32)) / 1000,
        ),
      );
      lastTime = time;
      paint();
      if (velocity()) frame = requestAnimationFrame(tick);
    }
    function updateScroll() {
      if (!velocity()) {
        cancelAnimationFrame(frame);
        frame = 0;
      } else if (!frame) {
        lastTime = performance.now();
        frame = requestAnimationFrame(tick);
      }
    }
    function move(event: PointerEvent) {
      if (event.pointerId !== pointerId) return;
      x = event.clientX;
      y = event.clientY;
      if (!active) {
        if (Math.hypot(x - originX, y - originY) < 5) return;
        // Let vertical touch gestures scroll rather than claiming the pointer.
        if (event.pointerType === "touch" && Math.abs(y - originY) > Math.abs(x - originX)) {
          finish(false);
          return;
        }
        active = true;
        suppressClick.current = true;
        control.setPointerCapture(pointerId);
        setDraggedId(id);
        element.classList.add("is-reordering");
        elements[from]!.classList.add("is-dragging");
      }
      paint();
      updateScroll();
    }
    function finish(commit: boolean, unmount = false) {
      stop.current = null;
      cancelAnimationFrame(frame);
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", cancel);
      window.removeEventListener("keydown", key, true);
      window.removeEventListener("blur", cancel);
      element.removeEventListener("scroll", scroll);
      control.removeEventListener("lostpointercapture", cancel);
      if (control.hasPointerCapture(pointerId)) control.releasePointerCapture(pointerId);
      element.classList.remove("is-reordering");
      elements.forEach((tab) => {
        tab.style.removeProperty("transform");
        tab.classList.remove("is-dragging");
      });
      if (!unmount) setDraggedId(null);
      if (commit && active && sameOrder()) {
        const remaining = positions.filter((_, index) => index !== from);
        latest.current.onDrop(id, remaining[to]?.id ?? null);
      }
    }
    function up(event: PointerEvent) {
      if (event.pointerId !== pointerId) return;
      x = event.clientX;
      y = event.clientY;
      if (active) paint();
      const bounds = element.getBoundingClientRect();
      finish(x >= bounds.left && x <= bounds.right && y >= bounds.top && y <= bounds.bottom);
    }
    function cancel(event: Event) {
      if (event instanceof PointerEvent && event.pointerId !== pointerId) return;
      finish(false);
    }
    function key(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      finish(false);
    }
    function scroll() {
      if (active) {
        element.scrollLeft = Math.max(0, Math.min(maxScroll, element.scrollLeft));
        paint();
        updateScroll();
      }
    }
    const pointerId = event.pointerId;
    stop.current = (unmount = false) => finish(false, unmount);
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", cancel);
    window.addEventListener("keydown", key, true);
    window.addEventListener("blur", cancel);
    element.addEventListener("scroll", scroll);
    control.addEventListener("lostpointercapture", cancel);
  }

  return {
    strip,
    draggedId,
    start,
    resetClick() {
      if (!stop.current) suppressClick.current = false;
    },
    suppressClick(event: MouseEvent) {
      if (!suppressClick.current || event.detail === 0) return;
      suppressClick.current = false;
      event.preventDefault();
      event.stopPropagation();
    },
  };
}
