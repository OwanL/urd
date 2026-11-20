import type { MessageContentCursor } from "../sessions/transcript/Messages.ts";
import type { MarkdownOwnedTextGeneration } from "./MarkdownClient.ts";
import type { MarkdownRenderSnapshot } from "./MarkdownRenderModel.ts";
import type { MarkdownWindowAnchor } from "./windowing/MarkdownWindowGeometry.ts";

export type MarkdownSavedSourcePosition = {
  readonly cursor: MessageContentCursor;
  readonly offset: number;
};

type MappableSource = Pick<MarkdownOwnedTextGeneration, "manifest" | "sourceCursor"> & {
  readonly isDisposed: boolean;
};

// Maps retained render offsets to wire cursors for one source lease at a time.
export class MarkdownSourceMappings {
  private mappingSource: MappableSource | null = null;
  private readonly sourceMappings = new Map<number, MessageContentCursor>();

  async read(
    source: MappableSource,
    capture: () => MarkdownWindowAnchor | null,
    isCurrent: () => boolean,
    signal: AbortSignal,
  ): Promise<MarkdownSavedSourcePosition | null> {
    signal.throwIfAborted();
    const anchor = capture();
    if (!anchor) return null;
    const sourceStart = Number(anchor.start);
    if (!Number.isSafeInteger(sourceStart)) throw new Error("Markdown source anchor is invalid.");
    const cached = this.mappingSource === source ? this.sourceMappings.get(sourceStart) : undefined;
    if (cached) return { cursor: cached, offset: anchor.offset };

    const cursor = await source.sourceCursor(sourceStart, signal);
    signal.throwIfAborted();
    if (isCurrent() && !source.isDisposed) {
      if (this.mappingSource !== source) this.sourceMappings.clear();
      this.mappingSource = source;
      this.sourceMappings.set(sourceStart, cursor);
    }
    return { cursor, offset: anchor.offset };
  }

  anchor(
    source: MappableSource,
    snapshot: MarkdownRenderSnapshot,
    saved: MarkdownSavedSourcePosition,
  ): MarkdownWindowAnchor | null {
    if (
      source.isDisposed ||
      this.mappingSource !== source ||
      snapshot.generation !== source.manifest.generation ||
      !Number.isFinite(saved.offset)
    )
      return null;

    const mapping = [...this.sourceMappings].find(([, cursor]) =>
      sameSourceCursor(cursor, saved.cursor),
    );
    const piece =
      mapping && [...snapshot.pieces.values()].find((item) => item.source.start === mapping[0]);
    if (!piece) return null;
    return {
      generation: snapshot.generation,
      piece: String(piece.id),
      start: String(piece.source.start),
      end: String(piece.source.end),
      offset: saved.offset,
    };
  }

  prune(source: MappableSource | null, snapshot: MarkdownRenderSnapshot): void {
    if (this.mappingSource !== source) {
      this.mappingSource = source;
      this.sourceMappings.clear();
    }
    // Mappings live only as long as their retained pieces, including native-selection pins.
    const starts = new Set([...snapshot.pieces.values()].map((piece) => piece.source.start));
    for (const start of this.sourceMappings.keys())
      if (!starts.has(start)) this.sourceMappings.delete(start);
  }

  clear(): void {
    this.mappingSource = null;
    this.sourceMappings.clear();
  }
}

function sameSourceCursor(left: MessageContentCursor, right: MessageContentCursor): boolean {
  return (
    left.segmentIndex === right.segmentIndex &&
    left.utf8Offset === right.utf8Offset &&
    left.message.executionId === right.message.executionId &&
    left.message.messageId === right.message.messageId &&
    left.message.historyRevision === right.message.historyRevision &&
    left.message.role === right.message.role
  );
}
