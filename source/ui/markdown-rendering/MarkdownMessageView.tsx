import type { MarkdownClientRequest } from "./MarkdownClient.ts";
import type { MarkdownRenderSnapshot } from "./MarkdownRenderModel.ts";
import {
  MarkdownSourceMappings,
  type MarkdownSavedSourcePosition,
} from "./MarkdownSourceMappings.ts";
import { MarkdownTextGenerations, type TrackedTextGeneration } from "./MarkdownTextGenerations.ts";
import { MarkdownWindowSurface } from "./MarkdownWindowSurface.tsx";
import { MarkdownGenerationHandoff } from "./windowing/MarkdownGenerationHandoff.ts";
import type { MarkdownWindowAnchor } from "./windowing/MarkdownWindowGeometry.ts";
import { anchorNearCursor } from "./windowing/MarkdownWindowPosition.ts";
import { prepareInitialWindow } from "./windowing/MarkdownWindowPreparation.ts";
import { refreshMarkdownWindow } from "./windowing/MarkdownWindowRefresh.ts";

export type MarkdownTextRequest = MarkdownClientRequest;
export type { MarkdownSavedSourcePosition } from "./MarkdownSourceMappings.ts";
export interface MarkdownMessageViewCallbacks {
  changed(): void;
  error(error: unknown): void;
}
export type MarkdownPublication = "published" | "cancelled";

type PendingRequest = {
  readonly request: MarkdownTextRequest;
  readonly saved: MarkdownSavedSourcePosition | undefined;
  readonly resolve: (outcome: MarkdownPublication) => void;
  readonly reject: (error: unknown) => void;
};

// Owns only the supplied empty host. The transcript remains the sole scrollport.
export class MarkdownMessageView {
  private readonly surface: MarkdownWindowSurface;
  private readonly retainedPieceCount: number;
  private readonly callbacks: MarkdownMessageViewCallbacks;
  private imageLayoutPending = false;
  private readonly generations = new MarkdownTextGenerations();
  private readonly mutationControllers = new Set<AbortController>();
  private readonly sourceReads = new Map<AbortController, Promise<unknown>>();
  private readonly sourceMappings = new MarkdownSourceMappings();
  private handoff: MarkdownGenerationHandoff | null = null;
  private activeGeneration: TrackedTextGeneration | null = null;
  private pendingRequest: PendingRequest | null = null;
  private requestController: AbortController | null = null;
  private requestWork: Promise<MarkdownPublication> | null = null;
  private mutationTail: Promise<void> = Promise.resolve();
  private reclaiming = false;
  private reclaimAgain = false;
  private paused = false;
  private disposed = false;
  private publicationVersion = 0;
  private activeSourceCaptures = 0;
  private disposal: Promise<void> | null = null;

  constructor(
    host: HTMLElement,
    scrollport: HTMLElement,
    retainedPieceCount: number,
    callbacks: MarkdownMessageViewCallbacks,
  ) {
    if (host.childNodes.length !== 0)
      throw new Error("Markdown message view requires an empty host element.");
    if (!Number.isSafeInteger(retainedPieceCount) || retainedPieceCount < 1)
      throw new RangeError("Markdown retained-piece count must be a positive integer.");
    this.retainedPieceCount = retainedPieceCount;
    this.callbacks = callbacks;
    this.surface = new MarkdownWindowSurface(host, scrollport, {
      snapshot: () => this.handoff?.snapshot,
      pinsChanged: () => {
        if (!this.paused && !this.disposed) {
          this.handoff?.resume();
          void this.reclaimPins();
        }
      },
      imagesChanged: () => this.imageChanged(),
    });
  }

  // The first request opens at the saved source cursor, or at the latest available source.
  // Later requests use generation handoff and retain the currently formatted DOM until ready.
  setRequest(
    request: MarkdownTextRequest,
    saved?: MarkdownSavedSourcePosition,
  ): Promise<MarkdownPublication> {
    if (this.disposed)
      return Promise.reject(
        new DOMException("The Markdown message view is disposed.", "AbortError"),
      );
    if (saved && !Number.isFinite(saved.offset))
      return Promise.reject(new RangeError("Markdown source offset must be finite."));

    let resolve!: (outcome: MarkdownPublication) => void;
    let reject!: (error: unknown) => void;
    const publication = new Promise<MarkdownPublication>((done, fail) => {
      resolve = done;
      reject = fail;
    });
    this.pendingRequest?.resolve("cancelled");
    this.pendingRequest = { request, saved, resolve, reject };
    this.requestController?.abort();
    this.handoff?.cancel();
    if (!this.paused) this.startPendingRequest();
    return publication;
  }

  // Pausing keeps the existing DOM mounted and fences all reads/publications. Releasing the
  // active lease is deliberately deferred: the current handoff owns that lease and old DOM.
  async setPaused(paused: boolean): Promise<void> {
    if (this.disposed || this.paused === paused) return;
    this.paused = paused;
    if (!paused) {
      if (this.imageLayoutPending) this.imageChanged();
      this.startPendingRequest();
      return;
    }

    await this.abortAndDrain();
  }

  // Captures the first visible piece's UTF-8 source start, never a JS string offset.
  async captureSource(signal: AbortSignal): Promise<MarkdownSavedSourcePosition | null> {
    const handoff = this.handoff;
    const source = this.activeGeneration;
    if (this.paused || this.disposed || !handoff || !source) return null;
    const linked = linkedController(signal);
    this.activeSourceCaptures++;
    const work = this.sourceMappings.read(
      source,
      () =>
        handoff.snapshot.generation === source.manifest.generation ? this.surface.capture() : null,
      () => this.activeGeneration === source,
      linked.controller.signal,
    );
    this.sourceReads.set(linked.controller, work);
    try {
      return await work;
    } finally {
      linked.dispose();
      this.sourceReads.delete(linked.controller);
      this.activeSourceCaptures--;
      if (!this.paused && !this.disposed) this.handoff?.resume();
    }
  }

  // Reflow of retained DOM needs no lease or new window. Only a cursor captured from
  // this exact active source can reuse its mapping; stale/unmounted anchors fail closed.
  restoreCapturedSource(saved: MarkdownSavedSourcePosition): boolean {
    const source = this.activeGeneration;
    const snapshot = this.handoff?.snapshot;
    if (this.paused || this.disposed || !this.surface.mounted || !source || !snapshot) return false;
    const anchor = this.sourceMappings.anchor(source, snapshot, saved);
    return anchor !== null && this.surface.restoreSource(anchor);
  }

  // Called by the parent at a visible scroll edge; no listener, timer or polling is installed.
  refreshWindow(): Promise<MarkdownRenderSnapshot | null> {
    return this.updateWindow(false);
  }

  private updateWindow(trimOnly: boolean): Promise<MarkdownRenderSnapshot | null> {
    if (this.paused || this.disposed || !this.handoff) return Promise.resolve(null);
    const controller = new AbortController();
    this.mutationControllers.add(controller);
    const mutation = this.enqueueMutation(() => {
      const handoff = this.handoff;
      if (this.paused || this.disposed || controller.signal.aborted || !handoff)
        return Promise.resolve(null);
      const version = this.publicationVersion;
      return refreshMarkdownWindow(
        handoff,
        this.surface,
        {
          trimOnly,
          pieceCount: this.activeGeneration?.manifest.pieceCount,
          retainedPieceCount: this.retainedPieceCount,
        },
        {
          isCurrent: () =>
            !this.paused &&
            !this.disposed &&
            this.publicationVersion === version &&
            this.handoff === handoff,
          publish: (snapshot, anchor) => this.publishSnapshot(snapshot, anchor),
        },
        controller.signal,
      );
    });
    return mutation
      .catch((error: unknown) => {
        if (!isAbortError(error) && !this.disposed) this.callbacks.error(error);
        throw error;
      })
      .finally(() => this.mutationControllers.delete(controller));
  }

  dispose(): Promise<void> {
    if (this.disposal) return this.disposal;
    this.disposed = true;
    this.surface.releaseImages();
    this.pendingRequest?.resolve("cancelled");
    this.pendingRequest = null;
    this.disposal = (async () => {
      let failure: unknown;
      try {
        await this.abortAndDrain();
        await this.handoff?.dispose();
      } catch (error) {
        failure = error;
      } finally {
        this.sourceMappings.clear();
        this.surface.unmount();
      }
      if (failure !== undefined) throw failure;
    })();
    return this.disposal;
  }

  private abortAndDrain(): Promise<PromiseSettledResult<unknown>[]> {
    this.requestController?.abort();
    this.handoff?.cancel();
    for (const controller of this.mutationControllers) controller.abort();
    for (const controller of this.sourceReads.keys()) controller.abort();
    return Promise.allSettled([this.requestWork, this.mutationTail, ...this.sourceReads.values()]);
  }

  private startPendingRequest(): void {
    const pending = this.pendingRequest;
    if (this.disposed || this.paused || !pending || this.requestWork) return;
    const controller = new AbortController();
    this.requestController = controller;
    const work = this.handoff
      ? this.replaceGeneration(pending, controller.signal)
      : this.initialize(pending, controller.signal);
    this.requestWork = work;
    void work
      .then(
        (outcome) => {
          if (this.pendingRequest === pending && !this.paused) {
            this.pendingRequest = null;
            pending.resolve(outcome);
          }
        },
        (error: unknown) => {
          if (this.pendingRequest === pending && !this.paused) {
            this.pendingRequest = null;
            pending.reject(error);
            this.callbacks.error(error);
          }
        },
      )
      .finally(() => {
        if (this.requestWork === work) {
          this.requestWork = null;
          this.requestController = null;
        }
        this.startPendingRequest();
      });
  }

  private async initialize(
    pending: PendingRequest,
    signal: AbortSignal,
  ): Promise<MarkdownPublication> {
    let source: TrackedTextGeneration | null = null;
    try {
      source = await this.generations.open(pending.request, signal);
      const { window, location } = await prepareInitialWindow(
        source,
        pending.saved?.cursor,
        this.retainedPieceCount,
        signal,
      );
      signal.throwIfAborted();
      if (this.disposed || this.paused || this.pendingRequest !== pending) return "cancelled";

      this.activeGeneration = source;
      this.handoff = new MarkdownGenerationHandoff(source, window, {
        capture: () => this.surface.capture(),
        pinned: () => this.activeSourceCaptures > 0 || this.surface.selectionBlocked,
        publish: (nextSnapshot, anchor) => {
          this.activeGeneration = this.generations.find(nextSnapshot.generation);
          this.publicationVersion++;
          this.publishSnapshot(nextSnapshot, anchor);
        },
      });
      this.publishSnapshot(
        window.snapshot,
        pending.saved ? anchorNearCursor(window.snapshot, location, pending.saved.offset) : null,
      );
      source = null;
      return "published";
    } catch (error) {
      if (source) {
        try {
          await source.dispose();
        } catch (releaseError) {
          throw new AggregateError(
            [error, releaseError],
            "Markdown initialization and release failed.",
          );
        }
      }
      if (isAbortError(error) && signal.aborted) return "cancelled";
      throw error;
    }
  }

  private async replaceGeneration(
    pending: PendingRequest,
    signal: AbortSignal,
  ): Promise<MarkdownPublication> {
    const handoff = this.handoff;
    if (!handoff) return "cancelled";
    const { request } = pending;
    return handoff.request(
      { open: (readSignal) => this.generations.open(request, readSignal) },
      signal,
    );
  }

  private imageChanged(): void {
    if (this.disposed) return;
    this.imageLayoutPending = this.paused;
    if (this.paused || !this.surface.mounted || !this.handoff) return;
    try {
      const anchor = this.surface.capture();
      this.publishSnapshot(this.handoff.snapshot, anchor);
    } catch (error) {
      this.callbacks.error(error);
    }
  }

  private publishSnapshot(
    snapshot: MarkdownRenderSnapshot,
    anchor: MarkdownWindowAnchor | null,
  ): void {
    this.sourceMappings.prune(this.activeGeneration, snapshot);
    this.surface.publish(snapshot, anchor, this.activeGeneration?.imageLoader ?? null);
    this.publicationVersion++;
    this.callbacks.changed();
  }

  private async reclaimPins(): Promise<void> {
    this.reclaimAgain = true;
    if (this.reclaiming) return;
    this.reclaiming = true;
    try {
      do {
        this.reclaimAgain = false;
        await this.updateWindow(true);
      } while (this.reclaimAgain && !this.paused && !this.disposed);
    } catch {
      // updateWindow reports failures through the owner's error callback.
    } finally {
      this.reclaiming = false;
    }
  }

  private enqueueMutation<T>(operation: () => Promise<T>): Promise<T> {
    const next = this.mutationTail.then(operation, operation);
    this.mutationTail = next.then(
      () => undefined,
      () => undefined,
    );
    return next;
  }
}

function linkedController(signal: AbortSignal): { controller: AbortController; dispose(): void } {
  const controller = new AbortController();
  const abort = () => controller.abort(signal.reason);
  if (signal.aborted) abort();
  else signal.addEventListener("abort", abort, { once: true });
  return { controller, dispose: () => signal.removeEventListener("abort", abort) };
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}
