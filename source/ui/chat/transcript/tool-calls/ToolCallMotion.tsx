import type { ComponentChildren } from "preact";
import { useLayoutEffect, useRef, useState } from "preact/hooks";
import type { DraftPreview } from "./ToolDraftPresentation.ts";

export function ToolCallMotion({
  children,
  motion,
  enter,
  preview = null,
  continueBatch = false,
}: {
  children: ComponentChildren;
  motion: boolean;
  enter: boolean;
  preview?: DraftPreview | null;
  continueBatch?: boolean;
}) {
  const [entering, setEntering] = useState(
    () => enter && !window.matchMedia("(prefers-reduced-motion: reduce)").matches,
  );
  const lastPreview = useRef<DraftPreview | null>(null);
  const [, refresh] = useState(0);
  const panel = useRef<HTMLDivElement>(null);
  const content = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (!motion) setEntering(false);
  }, [motion]);

  const outgoing =
    !preview &&
    lastPreview.current &&
    motion &&
    !window.matchMedia("(prefers-reduced-motion: reduce)").matches
      ? lastPreview.current
      : null;
  useLayoutEffect(() => {
    if (preview || !outgoing) {
      lastPreview.current = preview;
      return;
    }
    // Only the outgoing pixels linger; status and accessibility change immediately.
    const timer = window.setTimeout(() => {
      lastPreview.current = null;
      refresh((version) => version + 1);
    }, 250);
    return () => window.clearTimeout(timer);
  }, [preview, outgoing]);

  const open = preview !== null;
  useLayoutEffect(() => {
    const node = panel.current;
    const body = content.current;
    if (!open || !node || !body) return;
    const limit = Number.parseFloat(
      getComputedStyle(node).getPropertyValue("--draft-preview-limit"),
    );
    let measuredHeight = -1;
    const measure = () => {
      const height = body.offsetHeight;
      if (height === measuredHeight) return;
      measuredHeight = height;
      node.style.setProperty("--draft-preview-size", `${height}px`);
      node.dataset.overflow = String(height > limit);
    };
    const observer = new ResizeObserver(measure);
    observer.observe(body);
    measure();
    return () => observer.disconnect();
  }, [open]);

  const displayed = preview ?? outgoing;

  return (
    <div class="tool-call-motion" data-tool-motion={motion}>
      <div
        class="tool-call-reveal"
        data-entering={motion && entering}
        onAnimationEnd={(event) => {
          if (event.target === event.currentTarget) setEntering(false);
        }}
      >
        <div class="tool-call-header">{children}</div>
      </div>
      {displayed && (
        <div
          ref={panel}
          class="tool-row-draft-preview"
          data-preview-style={displayed.style}
          data-open={open}
          data-continue-batch={continueBatch}
          aria-hidden={!open}
        >
          <div ref={content} class="tool-row-draft-preview-content">
            {displayed.style === "edit"
              ? displayed.parts.map(({ kind, text }, index) => (
                  <span key={index} class={`tool-row-draft-${kind}`}>
                    {index > 0 ? `\n${text}` : text}
                  </span>
                ))
              : displayed.text}
          </div>
        </div>
      )}
    </div>
  );
}
