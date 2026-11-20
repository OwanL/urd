import type { MarkdownClientRequest, MarkdownOwnedTextGeneration } from "./MarkdownClient.ts";
import type { MarkdownImageLoader } from "./MarkdownRenderModel.ts";

export type TrackedTextGeneration = MarkdownOwnedTextGeneration & {
  readonly isDisposed: boolean;
  readonly imageLoader: MarkdownImageLoader | null;
};

// Tracks open leases for publication, including overlapping leases of the same generation.
// The caller/handoff still decides when each lease is released.
export class MarkdownTextGenerations {
  private readonly ownedGenerations = new Set<TrackedTextGeneration>();

  async open(request: MarkdownClientRequest, signal: AbortSignal): Promise<TrackedTextGeneration> {
    const source = await request.open(signal);
    let isDisposed = false;
    let release: Promise<void> | null = null;
    const tracked: TrackedTextGeneration = {
      manifest: source.manifest,
      source: source.source,
      imageLoader: request.imageLoader,
      get isDisposed() {
        return isDisposed;
      },
      locate: (offset, signal) => source.locate(offset, signal),
      sourceCursor: (offset, signal) => source.sourceCursor(offset, signal),
      locateChild: (contextId, childIndex, signal) =>
        source.locateChild(contextId, childIndex, signal),
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

  find(generation: string): TrackedTextGeneration | null {
    return (
      [...this.ownedGenerations]
        .reverse()
        .find((owned) => !owned.isDisposed && owned.manifest.generation === generation) ?? null
    );
  }
}
