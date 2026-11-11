import type {
  MarkdownGenerationRequest,
  MarkdownOwnedGeneration,
  MarkdownPieceInterval,
  MarkdownRenderCursor,
  MarkdownRenderSnapshot,
  MarkdownTrimRequest,
} from "./MarkdownRenderModel.ts";
import { copyMarkdownManifest, MarkdownWindowAssembler } from "./MarkdownWindowAssembler.ts";
import type { MarkdownWindowAnchor, MarkdownWindowSeekTarget } from "./MarkdownWindowGeometry.ts";

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
  private releaseFailure: { error: unknown } | null = null;
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

  seek(
    target: MarkdownWindowSeekTarget,
    windowSize: number,
    retain: readonly MarkdownPieceInterval[],
    prepare: (
      window: MarkdownWindowAssembler,
      cursor: MarkdownRenderCursor,
      signal: AbortSignal,
    ) => Promise<{ snapshot: MarkdownRenderSnapshot; anchor: MarkdownWindowAnchor | null }>,
    isCurrent: () => boolean,
    signal: AbortSignal,
  ): Promise<MarkdownRenderSnapshot> {
    if (this.disposed || !this.activeWindowOpen)
      return Promise.reject(new DOMException("The Markdown window is unavailable.", "AbortError"));
    if (!Number.isSafeInteger(windowSize) || windowSize < 1)
      return Promise.reject(new RangeError("Markdown seek window size must be positive."));
    if (target.generation !== this.active.manifest.generation)
      return Promise.reject(
        new Error("Markdown render generation changed; restart from the source anchor."),
      );

    const controller = new AbortController();
    const abort = () => controller.abort(signal.reason);
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
    const active = this.active;
    const previous = this.window;
    const pending = Promise.resolve().then(async () => {
      controller.signal.throwIfAborted();
      if (!isCurrent())
        throw new DOMException("The Markdown seek target changed before lookup.", "AbortError");
      const cursor: MarkdownRenderCursor =
        "index" in target
          ? { generation: target.generation, index: target.index }
          : await active.locateChild(target.contextId, target.childIndex, controller.signal);
      controller.signal.throwIfAborted();
      if (
        cursor.generation !== target.generation ||
        !Number.isSafeInteger(cursor.index) ||
        cursor.index < 0 ||
        cursor.index > active.manifest.pieceCount ||
        ("index" in target && cursor.index === active.manifest.pieceCount)
      )
        throw new Error("Markdown seek cursor does not match its target generation.");
      const from = seekFrom(cursor.index, active.manifest.pieceCount, windowSize);
      const window = new MarkdownWindowAssembler(active, active.manifest, from, {
        snapshot: previous.snapshot,
        ranges: retain,
      });
      const prepared = await prepare(window, cursor, controller.signal);
      controller.signal.throwIfAborted();
      if (!isCurrent())
        throw new DOMException(
          "The Markdown seek target changed before publication.",
          "AbortError",
        );
      if (
        this.disposed ||
        !this.activeWindowOpen ||
        this.active !== active ||
        this.window !== previous
      )
        throw new DOMException("The Markdown window changed during seek.", "AbortError");
      if (
        prepared.snapshot !== window.snapshot ||
        prepared.snapshot.generation !== target.generation ||
        (prepared.anchor && prepared.anchor.generation !== target.generation)
      )
        throw new Error("Markdown seek preparation did not match its generation.");

      this.window = window;
      try {
        this.view.publish(prepared.snapshot, prepared.anchor);
      } catch (error) {
        this.window = previous;
        throw error;
      }
      return prepared.snapshot;
    });
    const tracked = pending.finally(() => {
      signal.removeEventListener("abort", abort);
      this.activeOperations.delete(controller);
    });
    this.activeOperations.set(controller, tracked);
    return tracked;
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
    const candidate = this.candidate;
    if (this.view.pinned()) {
      this.reading?.abort();
      return;
    }
    if (this.reading) return;
    const controller = new AbortController();
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
    await this.disposeOwned(this.active);
    if (this.releaseFailure) throw this.releaseFailure.error;
  }

  private async disposeOwned(source: MarkdownOwnedGeneration): Promise<void> {
    try {
      await source.dispose();
    } catch (error) {
      this.releaseFailure ??= { error };
    }
  }

  private reject(candidate: Candidate, error: unknown): void {
    if (this.candidate !== candidate) return;
    this.candidate = null;
    candidate.detach();
    candidate.reject(error);
  }

  private async replace(candidate: Candidate, controller: AbortController): Promise<void> {
    let source: MarkdownOwnedGeneration | null = null;
    const signal = controller.signal;
    try {
      signal.throwIfAborted();
      if (this.releaseFailure) throw this.releaseFailure.error;
      const prior = this.snapshot;
      const anchor = this.view.capture();
      const oldPiece = anchor?.generation === prior.generation ? Number(anchor.piece) : prior.from;
      const sourceOffset =
        prior.pieces.get(oldPiece)?.source.start ?? prior.pieces.get(prior.from)?.source.start ?? 0;
      source = await candidate.request.open(signal);
      signal.throwIfAborted();
      const manifest = copyMarkdownManifest(source.manifest);
      if (
        manifest.generation === prior.generation &&
        (manifest.sourceUtf8Bytes !== this.active.manifest.sourceUtf8Bytes ||
          manifest.pieceCount !== this.active.manifest.pieceCount ||
          manifest.contextCount !== this.active.manifest.contextCount)
      )
        throw new Error("Markdown replacement manifest differs from the active generation.");
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
      candidate.resolve("published");
      await this.disposeOwned(previous);
    } catch (error) {
      if (!signal.aborted) this.reject(candidate, error);
    } finally {
      if (source) await this.disposeOwned(source);
      if (this.reading === controller) this.reading = null;
      if (!this.disposed) this.activeWindowOpen = true;
      this.resume();
    }
  }
}

function seekFrom(index: number, pieceCount: number, windowSize: number): number {
  const count = Math.min(pieceCount, windowSize);
  return Math.min(Math.max(0, index - Math.floor(count / 2)), Math.max(0, pieceCount - count));
}
