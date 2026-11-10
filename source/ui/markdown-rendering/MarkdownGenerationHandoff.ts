import type {
  MarkdownGenerationRequest,
  MarkdownOwnedGeneration,
  MarkdownRenderSnapshot,
  MarkdownTrimRequest,
} from "./MarkdownRenderModel.ts";
import { copyMarkdownManifest, MarkdownWindowAssembler } from "./MarkdownWindowAssembler.ts";
import type { MarkdownWindowAnchor } from "./MarkdownWindowGeometry.ts";

export interface MarkdownHandoffView {
  capture(): MarkdownWindowAnchor | null;
  pinned(): boolean;
  // Synchronous, non-throwing commit: render, measure/re-render, then restore anchor.
  // Only after this returns does the owner release the previous source.
  publish(snapshot: MarkdownRenderSnapshot, anchor: MarkdownWindowAnchor | null): void;
}
type Outcome = "published" | "cancelled";
type Candidate = {
  request: MarkdownGenerationRequest;
  resolve: (outcome: Outcome) => void;
  reject: (error: unknown) => void;
  detach: () => void;
};

// One message, one displayed generation and at most one replacement read. Selection
// can defer catch-up indefinitely: only the latest unopened descriptor is retained.
// An open must honour abort to permit prompt drain; even late delivery is owned.
export class MarkdownGenerationHandoff {
  private active: MarkdownOwnedGeneration;
  private window: MarkdownWindowAssembler;
  private readonly view: MarkdownHandoffView;
  private candidate: Candidate | null = null;
  private reading: AbortController | null = null;
  private readonly activeOperations = new Map<AbortController, Promise<unknown>>();
  private activeWindowOpen = true;
  private candidateWork: Promise<void> | null = null;
  private disposal: Promise<void> | null = null;
  private disposed = false;

  // Transfers ownership of the initial source and displayed, idle window.
  constructor(
    initial: MarkdownOwnedGeneration,
    initialWindow: MarkdownWindowAssembler,
    view: MarkdownHandoffView,
  ) {
    const manifest = copyMarkdownManifest(initial.manifest);
    if (manifest.generation !== initialWindow.snapshot.generation)
      throw new Error("Markdown initial generation mismatch.");
    this.active = initial;
    this.window = initialWindow;
    this.view = view;
  }

  get snapshot(): MarkdownRenderSnapshot {
    return this.window.snapshot;
  }
  get deferred(): boolean {
    return this.candidate !== null && this.view.pinned();
  }

  append(signal: AbortSignal): Promise<MarkdownRenderSnapshot> {
    return this.operate((window, readSignal) => window.append(readSignal), signal);
  }

  prepend(signal: AbortSignal): Promise<MarkdownRenderSnapshot> {
    return this.operate((window, readSignal) => window.prepend(readSignal), signal);
  }

  trim(request: MarkdownTrimRequest, signal: AbortSignal): Promise<MarkdownRenderSnapshot> {
    return this.operate((window, readSignal) => window.trim(request, readSignal), signal);
  }

  // Settles only on publication, cancellation or failure, never on mere deferral.
  // Supersession/abort settles cancelled immediately; reads drain before another open.
  request(request: MarkdownGenerationRequest, signal?: AbortSignal): Promise<Outcome> {
    this.cancel();
    if (this.disposed || signal?.aborted) return Promise.resolve("cancelled");
    return new Promise((resolve, reject) => {
      const abort = () => this.cancel();
      this.candidate = {
        request,
        resolve,
        reject,
        detach: () => signal?.removeEventListener("abort", abort),
      };
      signal?.addEventListener("abort", abort, { once: true });
      this.resume();
    });
  }

  // Called by MarkdownSelectionPins' selectionchange/focus notifications.
  resume(): void {
    if (this.disposed || !this.candidate) return;
    if (this.view.pinned()) {
      this.reading?.abort();
      return;
    }
    if (this.reading) return;
    const controller = new AbortController();
    const candidate = this.candidate;
    this.reading = controller;
    let finish!: () => void;
    const work = new Promise<void>((resolve) => (finish = resolve));
    this.candidateWork = work;
    void work.then(() => {
      if (this.candidateWork === work) this.candidateWork = null;
    });
    void this.replace(candidate, controller).then(finish, finish);
  }

  cancel(): void {
    const candidate = this.candidate;
    this.candidate = null;
    this.reading?.abort();
    candidate?.detach();
    candidate?.resolve("cancelled");
  }

  dispose(): Promise<void> {
    if (this.disposal) return this.disposal;
    this.disposed = true;
    const candidateWork = this.candidateWork;
    this.disposal = Promise.resolve().then(() => this.releaseAfterDrain(candidateWork));
    this.cancel();
    return this.disposal;
  }

  private operate(
    operation: (
      window: MarkdownWindowAssembler,
      signal: AbortSignal,
    ) => Promise<MarkdownRenderSnapshot>,
    signal: AbortSignal,
  ): Promise<MarkdownRenderSnapshot> {
    if (this.disposed || !this.activeWindowOpen)
      return Promise.reject(new DOMException("The Markdown window is unavailable.", "AbortError"));
    const controller = new AbortController();
    const abort = () => controller.abort(signal.reason);
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
    const pending = Promise.resolve().then(() => operation(this.window, controller.signal));
    const tracked = pending.finally(() => {
      signal.removeEventListener("abort", abort);
      this.activeOperations.delete(controller);
    });
    this.activeOperations.set(controller, tracked);
    return tracked;
  }

  private async quiesceActiveWindow(): Promise<void> {
    this.activeWindowOpen = false;
    const active = [...this.activeOperations];
    for (const [controller] of active) controller.abort();
    await Promise.allSettled(active.map(([, operation]) => operation));
  }

  private async releaseAfterDrain(candidateWork: Promise<void> | null): Promise<void> {
    await Promise.all([this.quiesceActiveWindow(), candidateWork]);
    this.active.dispose();
  }

  private async replace(candidate: Candidate, controller: AbortController): Promise<void> {
    let source: MarkdownOwnedGeneration | null = null;
    const signal = controller.signal;
    try {
      signal.throwIfAborted();
      const prior = this.snapshot;
      const anchor = this.view.capture();
      const oldPiece = anchor?.generation === prior.generation ? Number(anchor.piece) : prior.from;
      const sourceOffset =
        prior.pieces.get(oldPiece)?.source.start ?? prior.pieces.get(prior.from)?.source.start ?? 0;
      source = await candidate.request.open(signal);
      signal.throwIfAborted();
      const manifest = copyMarkdownManifest(source.manifest);
      if (manifest.generation === prior.generation)
        throw new Error("Markdown replacement must have a new generation.");
      const cursor = await source.locate(Math.min(sourceOffset, manifest.sourceUtf8Bytes), signal);
      signal.throwIfAborted();
      if (
        cursor.generation !== manifest.generation ||
        !Number.isSafeInteger(cursor.index) ||
        cursor.index < 0 ||
        (manifest.pieceCount === 0 ? cursor.index !== 0 : cursor.index >= manifest.pieceCount)
      )
        throw new Error("Invalid Markdown located generation or cursor.");
      const count = Math.max(1, prior.next - prior.from);
      const relative = Math.max(0, Math.min(count - 1, oldPiece - prior.from));
      const from = Math.max(0, cursor.index - relative);
      const next = Math.min(manifest.pieceCount, from + count);
      const window = new MarkdownWindowAssembler(source, manifest, from);
      while (window.snapshot.next < next) await window.append(signal);
      await window.trim({ generation: manifest.generation, active: { from, next } }, signal);
      // Geometry may only render complete code lines/table rows. Expand only edge units;
      // reject an unbounded changed unit rather than silently loading a whole document.
      let first = from;
      let last = next;
      for (const { context } of window.snapshot.contexts.values())
        if (
          (context.kind === "codeLine" || context.kind === "row") &&
          !context.header &&
          context.firstPiece < next &&
          context.firstPiece + context.pieceCount > from
        ) {
          first = Math.min(first, context.firstPiece);
          last = Math.max(last, context.firstPiece + context.pieceCount);
        }
      if (last - first > count * 3)
        throw new Error("Markdown replacement unit exceeds its bounded window.");
      while (window.snapshot.from > first) await window.prepend(signal);
      while (window.snapshot.next < last) await window.append(signal);
      await window.trim(
        { generation: manifest.generation, active: { from: first, next: last } },
        signal,
      );
      signal.throwIfAborted();
      if (this.candidate !== candidate || this.disposed) return;
      if (this.view.pinned()) return;
      const located = window.snapshot.pieces.get(cursor.index);
      // Empty table cells can share the following visible piece's source start.
      // Locate correctly returns the first tie, but geometry needs a text rectangle.
      const piece = located?.text
        ? located
        : [...window.snapshot.pieces.values()]
            .filter((piece) => piece.text && piece.source.start === located?.source.start)
            .sort((a, b) => a.id - b.id)[0];
      const replacement =
        anchor && piece
          ? {
              generation: manifest.generation,
              piece: String(piece.id),
              start: String(piece.source.start),
              end: String(piece.source.end),
              offset: anchor.offset,
            }
          : null;
      await this.quiesceActiveWindow();
      signal.throwIfAborted();
      if (this.candidate !== candidate || this.disposed || this.view.pinned()) return;
      const previous = this.active;
      this.active = source;
      this.window = window;
      this.candidate = null;
      candidate.detach();
      this.view.publish(window.snapshot, replacement);
      source = null;
      previous.dispose();
      candidate.resolve("published");
    } catch (error) {
      if (this.candidate === candidate && !signal.aborted) {
        this.candidate = null;
        candidate.detach();
        candidate.reject(error);
      }
    } finally {
      source?.dispose();
      if (this.reading === controller) this.reading = null;
      if (!this.disposed) this.activeWindowOpen = true;
      this.resume();
    }
  }
}
