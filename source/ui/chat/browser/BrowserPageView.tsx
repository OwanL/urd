import { useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
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
  const textInput = useRef<HTMLTextAreaElement>(null);
  const composing = useRef(false);
  const streamConnected = useRef(false);
  const stream = useRef<BrowserPageStream | null>(null);
  const drawnRef = useRef(false);
  const [state, setState] = useState<BrowserPageStreamState>("connecting");
  const [drawn, setDrawn] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
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
        connected = next === "open";
        streamConnected.current = connected;
        composing.current = false;
        if (textInput.current) textInput.current.value = "";
        setState(next);
        setFailure(streamFailure);
        if (connected) {
          sendResize.current();
        } else {
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
    // CSS fits the last frame throughout a drag. Reconfigure Chromium's screencast
    // once the surface settles, not on every mouse move.
    let timer: ReturnType<typeof setTimeout>;
    const observer = new ResizeObserver(() => {
      clearTimeout(timer);
      timer = setTimeout(() => sendResize.current(), 80);
    });
    observer.observe(box);
    return () => {
      clearTimeout(timer);
      observer.disconnect();
    };
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
    textInput.current?.focus({ preventScroll: true });
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
    if (
      !streamConnected.current ||
      event.isComposing ||
      composing.current ||
      event.key === "Process"
    )
      return;
    const altGraph = event.getModifierState("AltGraph");
    // Paste is handled by the textarea's input event. Keep other host shortcuts
    // local, except selection and modified navigation within the remote page.
    const selectAll = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "a";
    if (!routedKeys.has(event.key) && !selectAll) return;
    if (altGraph) return;
    const modifiers = [
      event.ctrlKey && "Control",
      event.metaKey && "Meta",
      event.altKey && "Alt",
      event.shiftKey && "Shift",
    ].filter(Boolean);
    event.preventDefault();
    stream.current?.send({ type: "key", key: [...modifiers, event.key].join("+") });
  };

  const sendInput = (input: HTMLTextAreaElement) => {
    const text = input.value;
    input.value = "";
    if (streamConnected.current && text) stream.current?.send({ type: "text", text });
  };

  return (
    <div class="browser-page-view">
      <div
        ref={surface}
        class="browser-page-surface"
        tabindex={-1}
        role="application"
        aria-label={label}
        onFocus={(event) => {
          if (event.target === event.currentTarget)
            textInput.current?.focus({ preventScroll: true });
        }}
      >
        <textarea
          ref={textInput}
          class="browser-page-input-bridge"
          aria-label={`Input for ${page.title || page.url}`}
          tabindex={0}
          readOnly={state !== "open"}
          spellcheck={false}
          autocapitalize="off"
          autocomplete="off"
          onKeyDown={onKeyDown}
          onCompositionStart={() => {
            composing.current = streamConnected.current;
          }}
          onCompositionEnd={(event) => {
            const wasComposing = composing.current;
            composing.current = false;
            if (!wasComposing || !streamConnected.current) {
              event.currentTarget.value = "";
              return;
            }
            if (!event.currentTarget.value) event.currentTarget.value = event.data;
            sendInput(event.currentTarget);
          }}
          onInput={(event) => {
            if (!composing.current && !(event as InputEvent).isComposing)
              sendInput(event.currentTarget);
          }}
          onBeforeInput={(event) => {
            const input = event as InputEvent;
            if (!streamConnected.current || input.isComposing || composing.current) return;
            const key =
              input.inputType === "deleteContentBackward"
                ? "Backspace"
                : input.inputType === "deleteContentForward"
                  ? "Delete"
                  : null;
            if (!key) return;
            event.preventDefault();
            stream.current?.send({ type: "key", key });
          }}
        />
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
    </div>
  );
}
