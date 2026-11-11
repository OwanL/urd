import { useEffect, useRef, useState } from "preact/hooks";
import { MarkdownMessageView } from "../markdown-rendering/MarkdownMessageView.tsx";
import type { RichMessageBodyProps } from "./TranscriptDisplay.ts";
import "../markdown-rendering/markdown-renderer.css";

export function RichMessageBody(props: RichMessageBodyProps) {
  const host = useRef<HTMLDivElement>(null);
  const latest = useRef(props);
  latest.current = props;
  const update = useRef(() => {});
  const [error, setError] = useState<string | null>(null);
  const { display, root, sessionId, entry } = props;

  useEffect(() => {
    const element = host.current;
    if (!element || !root) return;
    let stopped = false;
    let visible = false;
    let view: MarkdownMessageView | null = null;
    let releasing = false;
    let reading = false;
    let queued = false;
    let applied = "";
    let frame = 0;
    const report = (cause: unknown) => {
      if (!stopped && !(cause instanceof DOMException && cause.name === "AbortError"))
        setError(cause instanceof Error ? cause.message : "Markdown read failed.");
    };
    const pinned = () => {
      const selection = element.ownerDocument.getSelection();
      return (
        element.contains(element.ownerDocument.activeElement) ||
        Boolean(
          selection &&
          !selection.isCollapsed &&
          selection.rangeCount &&
          selection.getRangeAt(0).intersectsNode(element),
        )
      );
    };
    function release() {
      if (!view || releasing) return;
      releasing = true;
      element!.style.minHeight = `${element!.getBoundingClientRect().height}px`;
      const work = view.dispose();
      display.release(work);
      void work
        .then(() => {}, report)
        .finally(() => {
          view = null;
          releasing = false;
          applied = "";
          schedule();
        });
    }
    async function refresh() {
      if (stopped || releasing) return;
      if (!visible && !latest.current.paused && !pinned()) {
        release();
        return;
      }
      const current = latest.current;
      const paused = current.paused || !current.streamId;
      if (!view && paused) return;
      view ??= new MarkdownMessageView(element!, root!, display.retainedPieces, {
        changed() {
          element!.style.minHeight = "";
          setError(null);
          schedule();
        },
        error: report,
      });
      await view.setPaused(paused);
      if (paused || stopped || releasing) return;
      if (reading) {
        queued = true;
        return;
      }
      const tail = current.text.availableThrough;
      const key = `${current.streamId}/${tail.segmentIndex}/${tail.utf8Offset}/${current.text.isClosed}`;
      reading = true;
      try {
        if (key !== applied) {
          applied = key;
          await view.setRequest(
            display.client.request(current.streamId!, sessionId, current.entry),
          );
        } else {
          const port = root!.getBoundingClientRect();
          const gapVisible = [
            ...element!.querySelectorAll<HTMLElement>("[data-markdown-gap]"),
          ].some((gap) => {
            const rect = gap.getBoundingClientRect();
            return rect.bottom > port.top && rect.top < port.bottom;
          });
          if (visible && gapVisible) await view.refreshWindow();
        }
      } catch (cause) {
        report(cause);
      } finally {
        reading = false;
        if (queued) {
          queued = false;
          schedule();
        }
      }
    }
    function schedule() {
      if (!stopped && !frame)
        frame = requestAnimationFrame(() => {
          frame = 0;
          void refresh().catch(report);
        });
    }
    function selectionChanged() {
      if (!latest.current.paused) schedule();
    }
    update.current = schedule;
    const observer = new IntersectionObserver(
      ([intersection]) => {
        visible = intersection?.isIntersecting ?? false;
        schedule();
      },
      { root },
    );
    observer.observe(element);
    root.addEventListener("scroll", schedule, { passive: true });
    element.ownerDocument.addEventListener("selectionchange", selectionChanged);
    element.addEventListener("focusout", schedule);
    return () => {
      stopped = true;
      update.current = () => {};
      observer.disconnect();
      root.removeEventListener("scroll", schedule);
      element.ownerDocument.removeEventListener("selectionchange", selectionChanged);
      element.removeEventListener("focusout", schedule);
      cancelAnimationFrame(frame);
      release();
    };
  }, [
    display,
    root,
    sessionId,
    entry.executionId,
    entry.entryId,
    entry.kind,
    entry.historyRevision,
  ]);

  useEffect(() => update.current(), [props.paused, props.streamId, props.text]);
  return (
    <div class={`message message-${entry.kind === "user" ? "user" : "assistant"}`}>
      <div ref={host} style={{ minHeight: "1lh" }} />
      {error && <div class="inline-error">{error}</div>}
    </div>
  );
}
