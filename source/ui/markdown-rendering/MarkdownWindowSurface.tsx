import { render } from "preact";
import { MarkdownImageWindow } from "./images/MarkdownImageWindow.ts";
import { MarkdownRenderer } from "./MarkdownRenderer.tsx";
import type {
  MarkdownImageLoader,
  MarkdownPieceInterval,
  MarkdownRenderSnapshot,
} from "./MarkdownRenderModel.ts";
import { MarkdownSelectionPins } from "./MarkdownSelectionPins.ts";
import { normalizeIntervals } from "./windowing/MarkdownWindowAlignment.ts";
import {
  MarkdownWindowGeometry,
  visibleMarkdownGapDirection,
  type MarkdownWindowAnchor,
  type MarkdownWindowSeekTarget,
} from "./windowing/MarkdownWindowGeometry.ts";

type SurfaceCallbacks = {
  pinsChanged(): void;
  imagesChanged(): void;
};

// Owns the mounted Markdown DOM, its layout measurements and its selection listeners.
// The enclosing transcript owns the scrollport and the message view decides when to publish.
export class MarkdownWindowSurface {
  private readonly host: HTMLElement;
  private readonly scrollport: HTMLElement;
  private readonly callbacks: SurfaceCallbacks;
  private readonly geometry = new MarkdownWindowGeometry();
  private readonly images: MarkdownImageWindow;
  private root: HTMLElement | null = null;
  private pins: MarkdownSelectionPins | null = null;
  private published: {
    snapshot: MarkdownRenderSnapshot;
    imageLoader: MarkdownImageLoader | null;
  } | null = null;
  private lastScrollTop: number | null = null;

  constructor(host: HTMLElement, scrollport: HTMLElement, callbacks: SurfaceCallbacks) {
    this.host = host;
    this.scrollport = scrollport;
    this.callbacks = callbacks;
    this.images = new MarkdownImageWindow(callbacks.imagesChanged);
  }

  get mounted(): boolean {
    return this.root !== null;
  }

  capture(): MarkdownWindowAnchor | null {
    return this.root ? this.geometry.capture(this.root, this.scrollport) : null;
  }

  restoreSource(anchor: MarkdownWindowAnchor): boolean {
    if (!this.root) return false;
    const width = this.host.getBoundingClientRect().width;
    if (width <= 0) return false;
    this.geometry.configure(anchor.generation, width, "message");
    return this.geometry.restore(this.root, anchor, this.scrollport);
  }

  seekTarget(snapshot: MarkdownRenderSnapshot): MarkdownWindowSeekTarget | null {
    const width = this.host.getBoundingClientRect().width;
    if (!this.root) return null;
    if (width > 0) this.geometry.configure(snapshot.generation, width, "message");
    return this.geometry.seekTarget(this.root, this.scrollport, snapshot);
  }

  watchSeek(snapshot: MarkdownRenderSnapshot, target: MarkdownWindowSeekTarget): () => boolean {
    const top = this.scrollport.scrollTop;
    const root = this.root;
    return () => {
      if (Math.abs(this.scrollport.scrollTop - top) >= 0.5 || !root || root !== this.root)
        return false;
      return sameSeekTarget(this.geometry.seekTarget(root, this.scrollport, snapshot), target);
    };
  }

  get selectionBlocked(): boolean {
    return this.pins?.blocked() ?? false;
  }

  retainedPins(): readonly MarkdownPieceInterval[] {
    const intervals = [...(this.pins?.read() ?? [])];
    const active = this.host.ownerDocument.activeElement;
    const root = this.root;
    if (!root || !active || !root.contains(active)) return intervals;

    const piece = active.closest<HTMLElement>("[data-piece]");
    const descendant = piece ?? active.querySelector<HTMLElement>("[data-piece]");
    if (descendant && root.contains(descendant)) {
      const id = Number(descendant.dataset.piece);
      if (Number.isSafeInteger(id)) intervals.push({ from: id, next: id + 1 });
    }
    return normalizeIntervals(intervals);
  }

  visiblePieces(snapshot: MarkdownRenderSnapshot): MarkdownPieceInterval | null {
    const root = this.root;
    if (!root) return null;
    const port = this.scrollport.getBoundingClientRect();
    const rootRect = root.getBoundingClientRect();
    const top = Math.max(port.top, rootRect.top);
    const bottom = Math.min(port.bottom, rootRect.bottom);
    const left = Math.max(port.left, rootRect.left);
    const right = Math.min(port.right, rootRect.right);
    if (bottom <= top || right <= left) return null;
    const visible: number[] = [];
    for (const piece of root.querySelectorAll<HTMLElement>("[data-piece]")) {
      const rect = piece.getBoundingClientRect();
      const id = Number(piece.dataset.piece);
      if (
        Number.isSafeInteger(id) &&
        snapshot.pieces.has(id) &&
        rect.bottom > top &&
        rect.top < bottom &&
        rect.right > left &&
        rect.left < right
      )
        visible.push(id);
    }
    if (visible.length > 0) return { from: Math.min(...visible), next: Math.max(...visible) + 1 };
    return null;
  }

  readDirection(
    snapshot: MarkdownRenderSnapshot,
    visible: MarkdownPieceInterval | null,
  ): "forward" | "backward" {
    const top = this.scrollport.scrollTop;
    const movement = this.lastScrollTop === null ? 0 : top - this.lastScrollTop;
    this.lastScrollTop = top;
    if (this.root) {
      const direction = visibleMarkdownGapDirection(this.root, this.scrollport, snapshot, movement);
      if (direction) return direction;
    }
    if (movement !== 0) return movement > 0 ? "forward" : "backward";
    if (visible && visible.from - snapshot.from <= snapshot.next - visible.next) return "backward";
    return "forward";
  }

  publish(
    snapshot: MarkdownRenderSnapshot,
    anchor: MarkdownWindowAnchor | null,
    imageLoader: MarkdownImageLoader | null,
  ): number | undefined {
    const previous = this.published;
    const top = this.scrollport.scrollTop;
    try {
      this.renderSnapshot(snapshot, anchor, imageLoader);
      const restoredTop = this.scrollport.scrollTop;
      return restoredTop === top ? undefined : restoredTop;
    } catch (error) {
      // Rendering or measurement may already have changed the DOM. Rebuild the last
      // good surface while its lease is still owned, before rejecting the replacement.
      this.unmount();
      if (previous) {
        try {
          this.renderSnapshot(previous.snapshot, null, previous.imageLoader);
          this.scrollport.scrollTop = top;
          this.lastScrollTop = this.scrollport.scrollTop;
        } catch (restoreError) {
          this.unmount();
          throw new AggregateError(
            [error, restoreError],
            "Markdown publication and restoration failed.",
          );
        }
      }
      throw error;
    }
  }

  private renderSnapshot(
    snapshot: MarkdownRenderSnapshot,
    anchor: MarkdownWindowAnchor | null,
    imageLoader: MarkdownImageLoader | null,
  ): void {
    if (this.root?.dataset.markdownGeneration !== snapshot.generation) this.clearPins();
    const width = this.host.getBoundingClientRect().width;
    this.geometry.configure(snapshot.generation, width > 0 ? width : 1, "message");
    const images = {
      resources: this.images.update(snapshot, imageLoader),
      scrollport: this.scrollport,
    };
    const renderWindow = () => {
      render(
        <MarkdownRenderer snapshot={snapshot} geometry={this.geometry} images={images} />,
        this.host,
      );
      this.root = this.host.querySelector<HTMLElement>(".markdown-renderer");
    };
    renderWindow();
    if (this.root && this.geometry.measure(this.root, this.scrollport)) renderWindow();
    if (this.root) this.geometry.restore(this.root, anchor, this.scrollport);
    this.lastScrollTop = this.scrollport.scrollTop;
    this.published = { snapshot, imageLoader };
    if (this.root && !this.pins) {
      this.pins = new MarkdownSelectionPins(
        this.root,
        () => this.published!.snapshot,
        this.callbacks.pinsChanged,
      );
    }
  }

  releaseImages(): void {
    this.images.dispose();
  }

  unmount(): void {
    this.clearPins();
    this.images.dispose();
    this.published = null;
    this.root = null;
    render(null, this.host);
  }

  private clearPins(): void {
    this.pins?.dispose();
    this.pins = null;
  }
}

function sameSeekTarget(
  left: MarkdownWindowSeekTarget | null,
  right: MarkdownWindowSeekTarget,
): boolean {
  if (!left || left.generation !== right.generation || Math.abs(left.offset - right.offset) >= 0.5)
    return false;
  if ("index" in left) return "index" in right && left.index === right.index;
  return (
    "contextId" in right &&
    left.contextId === right.contextId &&
    left.childIndex === right.childIndex
  );
}
