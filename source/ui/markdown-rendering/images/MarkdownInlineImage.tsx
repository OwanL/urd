import { useEffect, useRef } from "preact/hooks";
import type { MarkdownImageResource } from "./MarkdownImageWindow.ts";

export function MarkdownInlineImage({
  alt,
  resource,
  scrollport,
  canOpen,
}: {
  alt: string;
  resource: MarkdownImageResource;
  scrollport: HTMLElement;
  canOpen: boolean;
}) {
  const target = useRef<HTMLSpanElement>(null);
  const state = resource.state;
  useEffect(() => {
    const element = target.current;
    if (!element || resource.state.kind !== "pending") return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((entry) => entry.isIntersecting)) return;
        observer.disconnect();
        void resource.load();
      },
      { root: scrollport, rootMargin: "160px 0px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [resource, scrollport]);

  if (state.kind !== "ready") {
    return (
      <span ref={target} className="markdown-renderer-image">
        {state.kind === "unavailable" ? markdownImageFallback(alt) : alt || "Image"}
      </span>
    );
  }
  const { url, width, height } = state;
  const ratio = width / height;
  const image = (
    <img
      src={url}
      alt={alt}
      width={width}
      height={height}
      style={{ "--markdown-image-ratio": ratio }}
      onError={() => resource.imageFailed(url)}
    />
  );
  return (
    <span ref={target} className="markdown-renderer-image">
      {canOpen && alt ? (
        <a href={url} target="_blank" rel="noopener noreferrer" aria-label={`Open image: ${alt}`}>
          {image}
        </a>
      ) : (
        image
      )}
    </span>
  );
}

export function markdownImageFallback(alt: string): string {
  return alt ? `${alt} (image unavailable)` : "Image unavailable";
}
