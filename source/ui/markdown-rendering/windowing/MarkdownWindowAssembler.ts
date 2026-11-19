import type {
  MarkdownRenderSnapshot,
  MarkdownPageSource,
  MarkdownTextPiece,
  MarkdownRenderManifest,
  MarkdownPieceInterval,
  MarkdownTrimRequest,
} from "../MarkdownRenderModel.ts";
import {
  copyMarkdownManifest,
  copyMarkdownPiecePage,
  copyMarkdownPiecePageBefore,
  isIndex,
} from "./MarkdownPageValidation.ts";
import { MarkdownAssemblyRead, normalizeRanges } from "./MarkdownAssemblyRead.ts";

// One assembler owns one disposable generation/window. Pages commit atomically;
// failure/abort leaves the last snapshot usable. Concurrent reads are rejected.
export class MarkdownWindowAssembler {
  private current: MarkdownRenderSnapshot;
  private reading = false;
  private readonly manifest: MarkdownRenderManifest;
  private readonly source: MarkdownPageSource;

  constructor(
    source: MarkdownPageSource,
    manifest: MarkdownRenderManifest,
    from = 0,
    retained?: {
      readonly snapshot: MarkdownRenderSnapshot;
      readonly ranges: readonly MarkdownPieceInterval[];
    },
  ) {
    this.manifest = copyMarkdownManifest(manifest);
    if (!isIndex(from) || from > this.manifest.pieceCount)
      throw new Error("Invalid Markdown render window.");
    if (retained && retained.snapshot.generation !== this.manifest.generation)
      throw new Error("Markdown retained pieces belong to a different generation.");
    const displayRanges = normalizeRanges(retained?.ranges ?? []);
    const pieces = new Map<number, MarkdownTextPiece>();
    for (const range of displayRanges) {
      if (
        !isIndex(range.from) ||
        !isIndex(range.next) ||
        range.from >= range.next ||
        range.next > this.manifest.pieceCount
      )
        throw new Error("Invalid Markdown retained interval.");
      for (let id = range.from; id < range.next; id++) {
        const piece = retained?.snapshot.pieces.get(id);
        if (!piece) throw new Error("Markdown retained interval is incomplete.");
        pieces.set(id, piece);
      }
    }
    this.source = source;
    this.current = {
      generation: this.manifest.generation,
      from,
      next: from,
      hasMore: from < this.manifest.pieceCount,
      displayRanges,
      supportRanges: [],
      contexts: new Map(),
      pieces,
    };
  }

  get snapshot(): MarkdownRenderSnapshot {
    return this.current;
  }

  async append(signal: AbortSignal): Promise<MarkdownRenderSnapshot> {
    if (this.reading) throw new Error("A Markdown render read is already pending.");
    if (!this.current.hasMore) return this.current;
    signal.throwIfAborted();
    this.reading = true;
    try {
      const read = new MarkdownAssemblyRead(this.source, this.current, this.manifest, signal);
      const cursor = { generation: this.current.generation, index: this.current.next };
      const page = copyMarkdownPiecePage(
        await this.source.readPieces(cursor, signal),
        cursor,
        this.manifest,
      );
      await read.add(page);
      signal.throwIfAborted();
      this.current = {
        ...this.current,
        next: page.next,
        hasMore: page.hasMore,
        ...read.snapshotRanges([
          ...this.current.displayRanges,
          { from: this.current.from, next: page.next },
        ]),
        pieces: read.pieces,
        contexts: read.contexts,
      };
      return this.current;
    } finally {
      this.reading = false;
    }
  }

  async prepend(signal: AbortSignal): Promise<MarkdownRenderSnapshot> {
    if (this.reading) throw new Error("A Markdown render read is already pending.");
    if (this.current.from === 0) return this.current;
    signal.throwIfAborted();
    this.reading = true;
    try {
      const read = new MarkdownAssemblyRead(this.source, this.current, this.manifest, signal);
      const cursor = { generation: this.current.generation, index: this.current.from };
      const page = copyMarkdownPiecePageBefore(
        await this.source.readPiecesBefore(cursor, signal),
        cursor,
        this.manifest,
      );
      await read.add(page);
      signal.throwIfAborted();
      this.current = {
        ...this.current,
        from: page.from,
        ...read.snapshotRanges([
          ...this.current.displayRanges,
          { from: page.from, next: this.current.next },
        ]),
        pieces: read.pieces,
        contexts: read.contexts,
      };
      return this.current;
    } finally {
      this.reading = false;
    }
  }

  async trim(request: MarkdownTrimRequest, signal: AbortSignal): Promise<MarkdownRenderSnapshot> {
    if (this.reading) throw new Error("A Markdown render read is already pending.");
    signal.throwIfAborted();
    if (request.generation !== this.current.generation)
      throw new Error("Markdown render generation changed; restart from the source anchor.");
    const active = { ...request.active };
    const ranges = [active, ...(request.retain ?? [])];
    for (const range of ranges)
      if (
        !isIndex(range.from) ||
        !isIndex(range.next) ||
        range.next < range.from ||
        range.next > this.manifest.pieceCount
      )
        throw new Error("Invalid Markdown retained interval.");
    if (active.from < this.current.from || active.next > this.current.next)
      throw new Error("Markdown trim active interval is outside the current window.");
    const displayRanges = normalizeRanges(ranges);
    const retained: MarkdownTextPiece[] = [];
    for (const { from, next } of displayRanges)
      for (let id = from; id < next; id++) {
        const piece = this.current.pieces.get(id);
        if (!piece)
          throw new Error("Markdown retained interval is incomplete; load it before trimming.");
        retained.push(piece);
      }
    // Start from primary pieces only. Rehydrate their closure into fresh maps rather
    // than retaining a historical context/piece cache behind the visible window.
    this.reading = true;
    try {
      const empty = { ...this.current, contexts: new Map(), pieces: new Map() };
      const read = new MarkdownAssemblyRead(
        this.source,
        empty,
        this.manifest,
        signal,
        this.current,
      );
      await read.retain(retained);
      signal.throwIfAborted();
      this.current = {
        generation: this.current.generation,
        from: active.from,
        next: active.next,
        hasMore: active.next < this.manifest.pieceCount,
        ...read.snapshotRanges(displayRanges),
        pieces: read.pieces,
        contexts: read.contexts,
      };
      return this.current;
    } finally {
      this.reading = false;
    }
  }
}
