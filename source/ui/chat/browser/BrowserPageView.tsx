import { useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../../controls/IconButton.tsx";
import type { BrowserPage } from "./BrowserPage.ts";
import { BrowserPageStream } from "./BrowserPageStream.ts";
import type { BrowserPageStreamState } from "./BrowserPageStream.ts";

interface BrowserPageViewProps {
  sessionId: string;
  page: BrowserPage;
  enabled: boolean;
}

// Keys forwarded as discrete key presses; everything printable goes through text
// so the page receives each character once.
const routedKeys = new Set([
  "Enter",
  "Backspace",
  "Delete",
  "Escape",
  "ArrowUp",
  "ArrowDown",
  "ArrowLeft",
  "ArrowRight",
  "Home",
  "End",
  "PageUp",
  "PageDown",
]);

// Frames arrive at their natural size and CSS contains them into the surface,
// so pointer coordinates map back through the same fit.
function mapPoint(
  canvas: HTMLCanvasElement,
  clientX: number,
  clientY: number,
): { x: number; y: number } | null {
  if (!canvas.width || !canvas.height) return null;
  const bounds = canvas.getBoundingClientRect();
  if (!bounds.width || !bounds.height) return null;
  return {
    x: clampPixel(((clientX - bounds.left) / bounds.width) * canvas.width, canvas.width),
    y: clampPixel(((clientY - bounds.top) / bounds.height) * canvas.height, canvas.height),
  };
}

function frameScale(canvas: HTMLCanvasElement): number {
  if (!canvas.width || !canvas.height) return 0;
  const bounds = canvas.getBoundingClientRect();
  return Math.min(bounds.width / canvas.width, bounds.height / canvas.height);
}

function clampPixel(value: number, extent: number): number {
  return Math.min(Math.max(Math.round(value), 0), extent - 1);
}

function paintFrame(canvas: HTMLCanvasElement, bitmap: ImageBitmap): boolean {
  if (canvas.width !== bitmap.width) canvas.width = bitmap.width;
  if (canvas.height !== bitmap.height) canvas.height = bitmap.height;
  const context = canvas.getContext("2d");
  if (!context) return false;
  context.drawImage(bitmap, 0, 0);
  return true;
}

export function BrowserPageView({ sessionId, page, enabled }: BrowserPageViewProps) {
  const surface = useRef<HTMLDivElement>(null);
  const frame = useRef<HTMLCanvasElement>(null);
  const textInput = useRef<HTMLInputElement>(null);
  const stream = useRef<BrowserPageStream | null>(null);
  const drawnRef = useRef(false);
  const [state, setState] = useState<BrowserPageStreamState>("connecting");
  const [drawn, setDrawn] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [text, setText] = useState("");
  const sendResize = useRef<() => void>(() => {});
  const pointer = useRef<{
    id: number;
    x: number;
    y: number;
    lastX: number;
    lastY: number;
    dragging: boolean;
    touch: boolean;
  } | null>(null);

  sendResize.current = () => {
    const box = surface.current;
    const width = box?.clientWidth ?? 0;
    const height = box?.clientHeight ?? 0;
    if (width > 0 && height > 0) stream.current?.send({ type: "resize", width, height });
  };

  useEffect(() => {
    const canvas = frame.current;
    if (canvas?.width) {
      canvas.getContext("2d")?.clearRect(0, 0, canvas.width, canvas.height);
      canvas.width = 1;
      canvas.height = 1;
    }
  }, [page.id, enabled]);

  useEffect(() => {
    if (!enabled) return;
    let live = true;
    let connected = false;
    let connectionVersion = 0;
    let painting = false;
    let latestFrame: { blob: Blob; connectionVersion: number } | null = null;
    drawnRef.current = false;
    setDrawn(false);

    // One decode in flight; frames landing mid-decode queue only the latest
    // so a busy stream cannot starve decoding or draw itself further behind.
    const paintQueuedFrames = async (): Promise<void> => {
      painting = true;
      try {
        while (live) {
          const queuedFrame = latestFrame;
          latestFrame = null;
          if (!queuedFrame) return;
          if (queuedFrame.connectionVersion !== connectionVersion) continue;
          let bitmap: ImageBitmap;
          try {
            bitmap = await createImageBitmap(queuedFrame.blob);
          } catch {
            continue; // Undecodable frames are dropped; the next frame repaints.
          }
          const canvas = frame.current;
          // Late images from an earlier page or a dropped connection are
          // discarded instead of painted over the current stream.
          if (
            live &&
            connected &&
            queuedFrame.connectionVersion === connectionVersion &&
            canvas?.isConnected &&
            paintFrame(canvas, bitmap)
          ) {
            if (!drawnRef.current) {
              drawnRef.current = true;
              setDrawn(true);
            }
          }
          bitmap.close();
        }
      } finally {
        painting = false;
      }
    };

    const pageStream = new BrowserPageStream(sessionId, page.id, {
      frame: (data) => {
        if (!live || !connected) return;
        latestFrame = { blob: data, connectionVersion };
        if (painting) return;
        void paintQueuedFrames();
      },
      stateChange: (next, streamFailure) => {
        setState(next);
        setFailure(streamFailure);
        if (next === "open") {
          connected = true;
          sendResize.current();
        } else {
          connected = false;
          connectionVersion += 1;
          latestFrame = null;
          drawnRef.current = false;
          setDrawn(false);
        }
      },
    });
    stream.current = pageStream;
    pageStream.open();
    return () => {
      live = false;
      pointer.current = null;
      stream.current = null;
      pageStream.close();
    };
  }, [sessionId, page.id, enabled]);

  useEffect(() => {
    if (!enabled || !surface.current) return;
    const box = surface.current;
    let queued = false;
    const observer = new ResizeObserver(() => {
      if (queued) return;
      queued = true;
      requestAnimationFrame(() => {
        queued = false;
        sendResize.current();
      });
    });
    observer.observe(box);
    return () => observer.disconnect();
  }, [enabled]);

  useLayoutEffect(() => {
    if (!enabled) return;
    const canvas = frame.current;
    if (!canvas) return;
    const onWheel = (event: WheelEvent) => {
      const point = mapPoint(canvas, event.clientX, event.clientY);
      const scale = frameScale(canvas);
      if (!point || !scale) return;
      event.preventDefault();
      stream.current?.send({
        type: "wheel",
        dx: event.deltaX / scale,
        dy: event.deltaY / scale,
        x: point.x,
        y: point.y,
      });
    };
    canvas.addEventListener("wheel", onWheel, { passive: false });
    return () => canvas.removeEventListener("wheel", onWheel);
  }, [enabled]);

  if (!enabled) return null;
  const label = `${page.title || page.url} browser page`;

  const onPointerDown = (event: PointerEvent) => {
    if (event.button !== 0 || pointer.current) return;
    const canvas = frame.current;
    if (!canvas) return;
    const point = mapPoint(canvas, event.clientX, event.clientY);
    if (!point) return;
    event.preventDefault();
    surface.current?.focus({ preventScroll: true });
    canvas.setPointerCapture(event.pointerId);
    pointer.current = {
      id: event.pointerId,
      x: event.clientX,
      y: event.clientY,
      lastX: event.clientX,
      lastY: event.clientY,
      dragging: false,
      touch: event.pointerType === "touch",
    };
  };

  const onPointerMove = (event: PointerEvent) => {
    const gesture = pointer.current;
    const canvas = frame.current;
    if (!gesture || gesture.id !== event.pointerId || !canvas) return;
    if (Math.hypot(event.clientX - gesture.x, event.clientY - gesture.y) > 8)
      gesture.dragging = true;
    if (gesture.dragging && gesture.touch) {
      const point = mapPoint(canvas, event.clientX, event.clientY);
      const scale = frameScale(canvas);
      if (point && scale)
        stream.current?.send({
          type: "wheel",
          dx: (gesture.lastX - event.clientX) / scale,
          dy: (gesture.lastY - event.clientY) / scale,
          x: point.x,
          y: point.y,
        });
    }
    gesture.lastX = event.clientX;
    gesture.lastY = event.clientY;
  };

  const onPointerUp = (event: PointerEvent) => {
    const gesture = pointer.current;
    if (!gesture || gesture.id !== event.pointerId) return;
    pointer.current = null;
    if (
      gesture.dragging ||
      Math.hypot(event.clientX - gesture.x, event.clientY - gesture.y) > 8 ||
      !frame.current
    )
      return;
    const point = mapPoint(frame.current, event.clientX, event.clientY);
    if (point) stream.current?.send({ type: "click", x: point.x, y: point.y });
  };

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.metaKey || ((event.ctrlKey || event.altKey) && !event.getModifierState("AltGraph")))
      return;
    if (event.key.length === 1) {
      event.preventDefault();
      stream.current?.send({ type: "text", text: event.key });
      return;
    }
    if (!routedKeys.has(event.key)) return;
    event.preventDefault();
    stream.current?.send({ type: "key", key: event.key });
  };

  const sendText = () => {
    if (!text) return;
    if (!stream.current?.send({ type: "text", text })) return;
    setText("");
    textInput.current?.focus({ preventScroll: true });
  };

  return (
    <div class="browser-page-view">
      <div
        ref={surface}
        class="browser-page-surface"
        tabindex={0}
        role="application"
        aria-label={label}
        onKeyDown={onKeyDown}
      >
        <canvas
          ref={frame}
          class="browser-page-frame"
          width={1}
          height={1}
          aria-hidden="true"
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={(event) => {
            if (pointer.current?.id === event.pointerId) pointer.current = null;
          }}
          onLostPointerCapture={(event) => {
            if (pointer.current?.id === event.pointerId) pointer.current = null;
          }}
        />
        {(state !== "open" || !drawn) && (
          <p class="browser-page-status" role="status">
            {state !== "open" ? (failure ?? "Connecting.") : "Waiting for first frame."}
          </p>
        )}
      </div>
      <div class="browser-page-text-row">
        <input
          ref={textInput}
          type="text"
          class="browser-page-text"
          aria-label={`Text for ${page.title || page.url}`}
          value={text}
          enterKeyHint="send"
          onInput={(event) => setText(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              sendText();
            }
          }}
        />
        <IconButton icon="send" label="Send text" onClick={sendText} />
      </div>
    </div>
  );
}
