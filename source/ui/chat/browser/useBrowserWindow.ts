import { useLayoutEffect, useRef, useState } from "preact/hooks";

type DockEdge = "top" | "bottom" | "left" | "right";
export type BrowserWindowMode = "floating" | DockEdge | "maximized";
export type ResizeEdge = "n" | "s" | "e" | "w" | "ne" | "nw" | "se" | "sw";
export const dividerEdge = { left: "e", right: "w", top: "s", bottom: "n" } as const;

interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}
interface Gesture {
  id: number;
  x: number;
  y: number;
  rect: Rect;
  edge?: ResizeEdge;
  split: number;
  mode: BrowserWindowMode;
  started: boolean;
  snapArmed: boolean;
}

const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), max);
const sideways = (edge: DockEdge) => edge === "left" || edge === "right";
const docked = (mode: BrowserWindowMode): mode is DockEdge =>
  mode !== "floating" && mode !== "maximized";

function fit(rect: Rect, bounds: DOMRect): Rect {
  const width = Math.min(rect.width, bounds.width);
  const height = Math.min(rect.height, bounds.height);
  return {
    width,
    height,
    x: clamp(rect.x, 0, bounds.width - width),
    y: clamp(rect.y, 0, bounds.height - height),
  };
}

function resize(rect: Rect, edge: ResizeEdge, dx: number, dy: number, bounds: DOMRect) {
  const right = rect.x + rect.width,
    bottom = rect.y + rect.height;
  const minWidth = Math.min(240, bounds.width),
    minHeight = Math.min(160, bounds.height);
  const x = edge.includes("w") ? clamp(rect.x + dx, 0, right - minWidth) : rect.x;
  const y = edge.includes("n") ? clamp(rect.y + dy, 0, bottom - minHeight) : rect.y;
  const width = (edge.includes("e") ? clamp(right + dx, x + minWidth, bounds.width) : right) - x;
  const height =
    (edge.includes("s") ? clamp(bottom + dy, y + minHeight, bounds.height) : bottom) - y;
  return fit({ x, y, width, height }, bounds);
}

function fitSplit(percent: number, mode: DockEdge, bounds: DOMRect) {
  const minimum = sideways(mode) ? 240 / bounds.width : 160 / bounds.height;
  const limit = clamp(minimum * 100, 20, 50);
  return clamp(percent, limit, 100 - limit);
}

function snapEdge(x: number, y: number, bounds: DOMRect): DockEdge | null {
  const distances: [DockEdge, number][] = [
    ["left", x - bounds.left],
    ["right", bounds.right - x],
    ["top", y - bounds.top],
    ["bottom", bounds.bottom - y],
  ];
  const nearest = distances.reduce((a, b) => (b[1] < a[1] ? b : a));
  return nearest[1] <= 24 ? nearest[0] : null;
}

export function useBrowserWindow(open: boolean) {
  const panel = useRef<HTMLElement>(null);
  const gesture = useRef<Gesture | null>(null);
  const [mode, setMode] = useState<BrowserWindowMode>("floating");
  const [rect, setRect] = useState<Rect | null>(null);
  const [preview, setPreview] = useState<DockEdge | null>(null);
  const [split, setSplit] = useState(50);

  useLayoutEffect(() => {
    const workspace = panel.current?.parentElement;
    if (!open || !workspace) {
      gesture.current = null;
      setPreview(null);
      return;
    }
    const keepInBounds = () => {
      const bounds = workspace.getBoundingClientRect();
      if (!bounds.width || !bounds.height) return;
      if (docked(mode)) setSplit((value) => fitSplit(value, mode, bounds));
      setRect((current) => {
        const next = fit(
          current ?? {
            x: (bounds.width - 720) / 2,
            y: (bounds.height - 480) / 2,
            width: 720,
            height: 480,
          },
          bounds,
        );
        return current &&
          next.x === current.x &&
          next.y === current.y &&
          next.width === current.width &&
          next.height === current.height
          ? current
          : next;
      });
    };
    keepInBounds();
    const observer = new ResizeObserver(keepInBounds);
    observer.observe(workspace);
    return () => observer.disconnect();
  }, [open, mode]);

  function place(next: BrowserWindowMode) {
    if (docked(next)) setSplit(50);
    setMode(next);
  }

  function start(event: PointerEvent, edge?: ResizeEdge) {
    const box = panel.current;
    const bounds = box?.parentElement?.getBoundingClientRect();
    if (!box || !bounds || !rect || gesture.current || event.button !== 0 || !event.isPrimary)
      return;
    if (!edge && event.target instanceof Element && event.target.closest("button, select, label"))
      return;
    const header = box.getBoundingClientRect();
    const pulledWidth = rect.width > bounds.width * 0.9 ? bounds.width * 0.85 : rect.width;
    const pulledHeight = rect.height > bounds.height * 0.9 ? bounds.height * 0.85 : rect.height;
    const origin =
      mode === "floating" || edge
        ? rect
        : {
            width: pulledWidth,
            height: pulledHeight,
            x:
              event.clientX -
              bounds.left -
              ((event.clientX - header.left) / header.width) * pulledWidth,
            y: header.top - bounds.top,
          };
    gesture.current = {
      id: event.pointerId,
      x: event.clientX,
      y: event.clientY,
      rect: origin,
      ...(edge && { edge }),
      split,
      mode,
      started: false,
      snapArmed: mode === "floating",
    };
    box.setPointerCapture(event.pointerId);
  }

  function move(event: PointerEvent, release = false) {
    const drag = gesture.current;
    if (!drag || drag.id !== event.pointerId) return;
    const bounds = panel.current?.parentElement?.getBoundingClientRect();
    if (!bounds?.width || !bounds.height) return;
    const dx = event.clientX - drag.x,
      dy = event.clientY - drag.y;
    if (!drag.started && Math.hypot(dx, dy) < 5) return;
    drag.started = true;
    if (!drag.edge) {
      const edge = snapEdge(event.clientX, event.clientY, bounds);
      // Allow direct drags along a workspace edge to a new dock. A short tug
      // should just restore the window, not immediately snap it back.
      if (!edge || Math.hypot(dx, dy) >= 48) drag.snapArmed = true;
      const snap = drag.snapArmed ? edge : null;
      setRect(fit({ ...drag.rect, x: drag.rect.x + dx, y: drag.rect.y + dy }, bounds));
      place(release && snap ? snap : "floating");
      setPreview(release ? null : snap);
    } else if (docked(drag.mode)) {
      const delta = sideways(drag.mode) ? dx / bounds.width : dy / bounds.height;
      const sign = drag.mode === "left" || drag.mode === "top" ? 1 : -1;
      setSplit(fitSplit(drag.split + sign * delta * 100, drag.mode, bounds));
    } else {
      setRect(resize(drag.rect, drag.edge, dx, dy, bounds));
    }
  }

  function end(event: PointerEvent) {
    if (gesture.current?.id !== event.pointerId) return;
    if (event.type === "pointerup") move(event, true);
    gesture.current = null;
    setPreview(null);
  }

  function onDividerKeyDown(event: KeyboardEvent) {
    const bounds = panel.current?.parentElement?.getBoundingClientRect();
    if (!docked(mode) || !bounds) return;
    const before = sideways(mode) ? "ArrowLeft" : "ArrowUp";
    const after = sideways(mode) ? "ArrowRight" : "ArrowDown";
    if (event.key !== before && event.key !== after) return;
    event.preventDefault();
    const sign = mode === "left" || mode === "top" ? 1 : -1;
    setSplit((value) => fitSplit(value + (event.key === after ? 5 : -5) * sign, mode, bounds));
  }

  return {
    panel,
    mode,
    place,
    rect: mode === "floating" ? rect : null,
    split,
    preview,
    onDividerKeyDown,
    onHeaderPointerDown: (event: PointerEvent) => start(event),
    onResizePointerDown: start,
    pointerEvents: {
      onPointerMove: (event: PointerEvent) => move(event),
      onPointerUp: end,
      onPointerCancel: end,
      onLostPointerCapture: end,
    },
  };
}
