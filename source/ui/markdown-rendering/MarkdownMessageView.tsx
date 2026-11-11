import { render } from "preact";
import type { MessageContentCursor } from "../sessions/Messages.ts";
import type { MarkdownClientRequest, MarkdownOwnedTextGeneration } from "./MarkdownClient.ts";
import { MarkdownGenerationHandoff } from "./MarkdownGenerationHandoff.ts";
import { MarkdownRenderer } from "./MarkdownRenderer.tsx";
import type {
  MarkdownPieceInterval,
  MarkdownRenderCursor,
  MarkdownRenderSnapshot,
  MarkdownTrimRequest,
} from "./MarkdownRenderModel.ts";
import { MarkdownSelectionPins } from "./MarkdownSelectionPins.ts";
import { MarkdownWindowAssembler } from "./MarkdownWindowAssembler.ts";
import { MarkdownWindowGeometry } from "./MarkdownWindowGeometry.ts";
import type { MarkdownWindowAnchor } from "./MarkdownWindowGeometry.ts";

export type MarkdownTextRequest = MarkdownClientRequest;
export type MarkdownSavedSourcePosition = {
  readonly cursor: MessageContentCursor;
  readonly offset: number;
};
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
type Direction = "forward" | "backward";
type ViewRange = MarkdownPieceInterval | null;
type TrackedTextGeneration = MarkdownOwnedTextGeneration & { readonly isDisposed: boolean };

// Owns only the supplied empty host. The transcript remains the sole scrollport.
export class MarkdownMessageView {
  private readonly host: HTMLElement;
  private readonly scrollport: HTMLElement;
  private readonly retainedPieceCount: number;
  private readonly callbacks: MarkdownMessageViewCallbacks;
  private readonly geometry = new MarkdownWindowGeometry();
  private readonly ownedGenerations = new Set<TrackedTextGeneration>();
  private readonly mutationControllers = new Set<AbortController>();
  private readonly sourceReads = new Map<AbortController, Promise<unknown>>();
  private pins: MarkdownSelectionPins | null = null;
  private rendererRoot: HTMLElement | null = null;
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
  private lastScrollTop: number | null = null;
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
    this.host = host;
    this.scrollport = scrollport;
    this.retainedPieceCount = retainedPieceCount;
    this.callbacks = callbacks;
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
      this.startPendingRequest();
      return;
    }

    this.requestController?.abort();
    this.handoff?.cancel();
    for (const controller of this.mutationControllers) controller.abort();
    for (const controller of this.sourceReads.keys()) controller.abort();
    await Promise.allSettled([
      ...(this.requestWork ? [this.requestWork] : []),
      this.mutationTail,
      ...this.sourceReads.values(),
    ]);
  }

  // Captures the first visible piece's UTF-8 source start, never a JS string offset.
  async captureSource(signal: AbortSignal): Promise<MarkdownSavedSourcePosition | null> {
    if (this.paused || this.disposed || !this.handoff || !this.activeGeneration) return null;
    const linked = linkedController(signal);
    this.activeSourceCaptures++;
    const work = this.readSourcePosition(linked.controller.signal);
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

  // Called by the parent at a visible scroll edge; no listener, timer or polling is installed.
  refreshWindow(): Promise<MarkdownRenderSnapshot | null> {
    return this.updateWindow(false);
  }

  private updateWindow(trimOnly: boolean): Promise<MarkdownRenderSnapshot | null> {
    if (this.paused || this.disposed || !this.handoff) return Promise.resolve(null);
    const controller = new AbortController();
    this.mutationControllers.add(controller);
    const mutation = this.enqueueMutation(async () => {
      if (this.paused || this.disposed || controller.signal.aborted || !this.handoff) return null;
      const handoff = this.handoff;
      const prior = handoff.snapshot;
      if (prior.from === prior.next) return prior;
      const version = this.publicationVersion;
      const visible = this.visiblePieces(prior);
      const direction = this.readDirection(prior, visible);
      const retained = this.currentPins();
      const loaded = trimOnly
        ? prior
        : direction === "forward"
          ? await handoff.append(controller.signal)
          : await handoff.prepend(controller.signal);
      if (controller.signal.aborted || this.paused || this.disposed) return null;
      if (this.publicationVersion !== version || this.handoff !== handoff) return null;

      const active = trimOnly
        ? { from: prior.from, next: prior.next }
        : refreshRange(loaded, direction, visible, this.retainedPieceCount);
      const trimmed = await this.alignAndTrim(handoff, loaded, active, retained, controller.signal);
      if (controller.signal.aborted || this.paused || this.disposed) return null;
      if (this.publicationVersion !== version || this.handoff !== handoff) return null;
      const width = this.host.getBoundingClientRect().width;
      if (this.rendererRoot && width > 0)
        this.geometry.configure(prior.generation, width, "message");
      const anchor = this.rendererRoot
        ? this.geometry.capture(this.rendererRoot, this.scrollport)
        : null;
      this.publishSnapshot(trimmed, anchor);
      return trimmed;
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
    this.pendingRequest?.resolve("cancelled");
    this.pendingRequest = null;
    this.requestController?.abort();
    this.handoff?.cancel();
    for (const controller of this.mutationControllers) controller.abort();
    for (const controller of this.sourceReads.keys()) controller.abort();

    this.disposal = (async () => {
      let failure: unknown;
      try {
        await Promise.allSettled([
          ...(this.requestWork ? [this.requestWork] : []),
          this.mutationTail,
          ...this.sourceReads.values(),
        ]);
        await this.handoff?.dispose();
      } catch (error) {
        failure = error;
      } finally {
        this.pins?.dispose();
        this.pins = null;
        this.rendererRoot = null;
        render(null, this.host);
      }
      if (failure !== undefined) throw failure;
    })();
    return this.disposal;
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
      source = this.trackGeneration(await pending.request.open(signal));
      signal.throwIfAborted();
      const location = await source.locateSource(
        pending.saved?.cursor ?? source.source.availableThrough,
        signal,
      );
      signal.throwIfAborted();
      const manifest = source.manifest;
      validateCursor(location, manifest.pieceCount, manifest.generation);
      const from = initialFrom(location.index, manifest.pieceCount, this.retainedPieceCount);
      const next = Math.min(manifest.pieceCount, from + this.retainedPieceCount);
      const window = new MarkdownWindowAssembler(source, manifest, from);
      while (window.snapshot.next < next) {
        signal.throwIfAborted();
        await window.append(signal);
      }
      const aligned = await this.alignWindow(window, window.snapshot, { from, next }, [], signal);
      const snapshot = await window.trim(
        { generation: manifest.generation, active: aligned.active },
        signal,
      );
      signal.throwIfAborted();
      if (this.disposed || this.paused || this.pendingRequest !== pending) return "cancelled";

      this.activeGeneration = source;
      this.handoff = new MarkdownGenerationHandoff(source, window, {
        capture: () =>
          this.rendererRoot ? this.geometry.capture(this.rendererRoot, this.scrollport) : null,
        pinned: () => this.activeSourceCaptures > 0 || (this.pins?.blocked() ?? false),
        publish: (nextSnapshot, anchor) => {
          this.activeGeneration = this.findGeneration(nextSnapshot.generation);
          this.publicationVersion++;
          this.publishSnapshot(nextSnapshot, anchor);
        },
      });
      this.publishSnapshot(
        snapshot,
        pending.saved ? this.savedAnchor(snapshot, location, pending.saved.offset) : null,
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
    return handoff.request(this.wrapRequest(pending.request), signal);
  }

  private wrapRequest(request: MarkdownTextRequest): MarkdownTextRequest {
    return {
      open: async (signal) => this.trackGeneration(await request.open(signal)),
    };
  }

  private trackGeneration(source: MarkdownOwnedTextGeneration): TrackedTextGeneration {
    let isDisposed = false;
    let release: Promise<void> | null = null;
    const tracked: TrackedTextGeneration = {
      manifest: source.manifest,
      source: source.source,
      get isDisposed() {
        return isDisposed;
      },
      locate: (offset, signal) => source.locate(offset, signal),
      sourceCursor: (offset, signal) => source.sourceCursor(offset, signal),
      locateSource: (cursor, signal) => source.locateSource(cursor, signal),
      readPieces: (cursor, signal) => source.readPieces(cursor, signal),
      readPiecesBefore: (cursor, signal) => source.readPiecesBefore(cursor, signal),
      readContext: (id, cursor, signal) => source.readContext(id, cursor, signal),
      readContexts: (ids, cursor, signal) => source.readContexts(ids, cursor, signal),
      dispose: () => {
        if (release) return release;
        isDisposed = true;
        this.ownedGenerations.delete(tracked);
        release = Promise.resolve(source.dispose());
        return release;
      },
    };
    this.ownedGenerations.add(tracked);
    return tracked;
  }

  private findGeneration(generation: string): TrackedTextGeneration | null {
    return (
      [...this.ownedGenerations]
        .reverse()
        .find((owned) => !owned.isDisposed && owned.manifest.generation === generation) ?? null
    );
  }

  private async readSourcePosition(
    signal: AbortSignal,
  ): Promise<MarkdownSavedSourcePosition | null> {
    signal.throwIfAborted();
    const root = this.rendererRoot;
    const source = this.activeGeneration;
    if (!root || !source || this.handoff?.snapshot.generation !== source.manifest.generation)
      return null;
    const anchor = this.geometry.capture(root, this.scrollport);
    if (!anchor) return null;
    const sourceStart = Number(anchor.start);
    if (!Number.isSafeInteger(sourceStart)) throw new Error("Markdown source anchor is invalid.");
    const cursor = await source.sourceCursor(sourceStart, signal);
    signal.throwIfAborted();
    return { cursor, offset: anchor.offset };
  }

  private async alignAndTrim(
    handoff: MarkdownGenerationHandoff,
    snapshot: MarkdownRenderSnapshot,
    active: MarkdownPieceInterval,
    retain: readonly MarkdownPieceInterval[],
    signal: AbortSignal,
  ): Promise<MarkdownRenderSnapshot> {
    const aligned = await this.alignWindow(
      {
        get snapshot() {
          return handoff.snapshot;
        },
        append: (readSignal) => handoff.append(readSignal),
        prepend: (readSignal) => handoff.prepend(readSignal),
      },
      snapshot,
      active,
      retain,
      signal,
    );
    const request: MarkdownTrimRequest = {
      generation: aligned.snapshot.generation,
      active: aligned.active,
      retain: aligned.retain,
    };
    return handoff.trim(request, signal);
  }

  private async alignWindow(
    window: Pick<MarkdownWindowAssembler, "snapshot" | "append" | "prepend">,
    initial: MarkdownRenderSnapshot,
    active: MarkdownPieceInterval,
    retain: readonly MarkdownPieceInterval[],
    signal: AbortSignal,
  ): Promise<{
    snapshot: MarkdownRenderSnapshot;
    active: MarkdownPieceInterval;
    retain: readonly MarkdownPieceInterval[];
  }> {
    let snapshot = initial;
    let activeRange = active;
    let retainedRanges = normalizeIntervals(retain);
    for (let attempt = 0; attempt < 8; attempt++) {
      signal.throwIfAborted();
      const expandedActive = expandToUnits(snapshot, activeRange, false);
      const expandedRetain = normalizeIntervals(
        retainedRanges.map((range) => expandToUnits(snapshot, range, true)),
      );
      if (expandedActive.next - expandedActive.from > this.retainedPieceCount * 3)
        throw new Error(
          "A complete Markdown code line or table row exceeds the active window bound.",
        );
      activeRange = expandedActive;
      retainedRanges = expandedRetain;

      while (snapshot.from > activeRange.from) {
        signal.throwIfAborted();
        snapshot = await window.prepend(signal);
      }
      while (snapshot.next < activeRange.next) {
        signal.throwIfAborted();
        snapshot = await window.append(signal);
      }
      for (const range of retainedRanges)
        for (let id = range.from; id < range.next; id++)
          if (!snapshot.pieces.has(id))
            throw new Error("A Markdown selection pin is outside the loaded window.");

      const checkedActive = expandToUnits(snapshot, activeRange, false);
      const checkedRetain = normalizeIntervals(
        retainedRanges.map((range) => expandToUnits(snapshot, range, true)),
      );
      if (
        checkedActive.from === activeRange.from &&
        checkedActive.next === activeRange.next &&
        sameIntervals(checkedRetain, retainedRanges)
      )
        return { snapshot, active: activeRange, retain: retainedRanges };
      activeRange = checkedActive;
      retainedRanges = checkedRetain;
    }
    throw new Error("Markdown window did not align to complete code lines and table rows.");
  }

  private currentPins(): readonly MarkdownPieceInterval[] {
    const snapshot = this.handoff?.snapshot;
    if (!snapshot) return [];
    const intervals = [...(this.pins?.read() ?? [])];
    const active = this.host.ownerDocument.activeElement;
    const root = this.rendererRoot;
    if (!root || !active || !root.contains(active)) return intervals;

    const piece = active.closest<HTMLElement>("[data-piece]");
    const descendant = piece ?? active.querySelector<HTMLElement>("[data-piece]");
    if (descendant && root.contains(descendant)) {
      const id = Number(descendant.dataset.piece);
      if (Number.isSafeInteger(id)) intervals.push({ from: id, next: id + 1 });
    }
    return normalizeIntervals(intervals);
  }

  private visiblePieces(snapshot: MarkdownRenderSnapshot): ViewRange {
    const root = this.rendererRoot;
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

  private readDirection(snapshot: MarkdownRenderSnapshot, visible: ViewRange): Direction {
    const top = this.scrollport.scrollTop;
    const movement = this.lastScrollTop === null ? 0 : top - this.lastScrollTop;
    this.lastScrollTop = top;
    const visibleRootGaps = this.rendererRoot
      ? [...this.rendererRoot.querySelectorAll<HTMLElement>('[data-markdown-gap="root"]')].filter(
          (gap) => {
            const rect = gap.getBoundingClientRect();
            const port = this.scrollport.getBoundingClientRect();
            return rect.bottom > port.top && rect.top < port.bottom;
          },
        )
      : [];
    if (movement !== 0) {
      const preferred: Direction = movement > 0 ? "forward" : "backward";
      if (
        visibleRootGaps.some((gap) =>
          preferred === "forward"
            ? Number(gap.dataset.gapFrom) >= snapshot.next
            : Number(gap.dataset.gapNext) <= snapshot.from,
        )
      )
        return preferred;
    }
    const after = visibleRootGaps.find((gap) => Number(gap.dataset.gapFrom) >= snapshot.next);
    if (after) return "forward";
    const before = visibleRootGaps.find((gap) => Number(gap.dataset.gapNext) <= snapshot.from);
    if (before) return "backward";
    if (movement !== 0) return movement > 0 ? "forward" : "backward";
    if (visible && visible.from - snapshot.from <= snapshot.next - visible.next) return "backward";
    return "forward";
  }

  private publishSnapshot(
    snapshot: MarkdownRenderSnapshot,
    anchor: MarkdownWindowAnchor | null,
  ): void {
    const previousGeneration = this.rendererRoot?.dataset.markdownGeneration;
    if (previousGeneration !== snapshot.generation) {
      this.pins?.dispose();
      this.pins = null;
    }
    const width = this.host.getBoundingClientRect().width;
    this.geometry.configure(snapshot.generation, width > 0 ? width : 1, "message");
    render(<MarkdownRenderer snapshot={snapshot} geometry={this.geometry} />, this.host);
    this.rendererRoot = this.host.querySelector<HTMLElement>(".markdown-renderer");
    if (this.rendererRoot && this.geometry.measure(this.rendererRoot, this.scrollport)) {
      render(<MarkdownRenderer snapshot={snapshot} geometry={this.geometry} />, this.host);
      this.rendererRoot = this.host.querySelector<HTMLElement>(".markdown-renderer");
    }
    if (this.rendererRoot) this.geometry.restore(this.rendererRoot, anchor, this.scrollport);
    this.lastScrollTop = this.scrollport.scrollTop;
    if (this.rendererRoot && !this.pins) {
      this.pins = new MarkdownSelectionPins(
        this.rendererRoot,
        () => this.handoff?.snapshot ?? snapshot,
        () => {
          if (!this.paused && !this.disposed) {
            this.handoff?.resume();
            void this.reclaimPins();
          }
        },
      );
    }
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

  private savedAnchor(
    snapshot: MarkdownRenderSnapshot,
    cursor: MarkdownRenderCursor,
    offset: number,
  ): MarkdownWindowAnchor | null {
    const piece = [...snapshot.pieces.values()]
      .filter((candidate) => candidate.text.length > 0)
      .sort(
        (left, right) => Math.abs(left.id - cursor.index) - Math.abs(right.id - cursor.index),
      )[0];
    if (!piece) return null;
    return {
      generation: snapshot.generation,
      piece: String(piece.id),
      start: String(piece.source.start),
      end: String(piece.source.end),
      offset,
    };
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

function initialFrom(index: number, pieceCount: number, retainedPieceCount: number): number {
  const start = Math.max(0, index - Math.floor(retainedPieceCount / 2));
  return Math.min(start, Math.max(0, pieceCount - retainedPieceCount));
}

function validateCursor(
  cursor: MarkdownRenderCursor,
  pieceCount: number,
  generation: string,
): void {
  if (
    !cursor ||
    cursor.generation !== generation ||
    !Number.isSafeInteger(cursor.index) ||
    cursor.index < 0 ||
    (pieceCount === 0 ? cursor.index !== 0 : cursor.index >= pieceCount)
  )
    throw new Error("Markdown source cursor does not match its render generation.");
}

function expandToUnits(
  snapshot: MarkdownRenderSnapshot,
  range: MarkdownPieceInterval,
  includeHeader: boolean,
): MarkdownPieceInterval {
  let from = range.from;
  let next = range.next;
  for (const { context } of snapshot.contexts.values()) {
    if (
      (context.kind !== "codeLine" && context.kind !== "row") ||
      (!includeHeader && context.header) ||
      context.pieceCount === 0
    )
      continue;
    const unitNext = context.firstPiece + context.pieceCount;
    if (context.firstPiece < next && unitNext > from) {
      from = Math.min(from, context.firstPiece);
      next = Math.max(next, unitNext);
    }
  }
  return { from, next };
}

function normalizeIntervals(intervals: readonly MarkdownPieceInterval[]): MarkdownPieceInterval[] {
  const sorted = [...intervals]
    .filter(
      ({ from, next }) => Number.isSafeInteger(from) && Number.isSafeInteger(next) && next > from,
    )
    .sort((left, right) => left.from - right.from);
  const result: MarkdownPieceInterval[] = [];
  for (const interval of sorted) {
    const last = result.at(-1);
    if (!last || interval.from > last.next) result.push({ ...interval });
    else result[result.length - 1] = { from: last.from, next: Math.max(last.next, interval.next) };
  }
  return result;
}

function sameIntervals(
  left: readonly MarkdownPieceInterval[],
  right: readonly MarkdownPieceInterval[],
): boolean {
  return (
    left.length === right.length &&
    left.every(
      (range, index) => range.from === right[index]?.from && range.next === right[index]?.next,
    )
  );
}

function refreshRange(
  loaded: MarkdownRenderSnapshot,
  direction: Direction,
  visible: ViewRange,
  retainedPieceCount: number,
): MarkdownPieceInterval {
  if (direction === "forward") {
    let from = Math.max(loaded.from, loaded.next - retainedPieceCount);
    if (visible && visible.from >= loaded.from && visible.from < loaded.next)
      from = Math.min(from, visible.from);
    let next = Math.min(loaded.next, from + retainedPieceCount);
    if (visible && visible.next > next) {
      next = visible.next;
      from = Math.max(loaded.from, next - retainedPieceCount);
    }
    return { from, next };
  }

  let from = loaded.from;
  let next = Math.min(loaded.next, from + retainedPieceCount);
  if (visible && visible.next > next) {
    next = visible.next;
    from = Math.max(loaded.from, next - retainedPieceCount);
  }
  if (visible && visible.from < from) {
    from = visible.from;
    next = Math.min(loaded.next, from + retainedPieceCount);
  }
  return { from, next };
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
