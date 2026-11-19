// Transaction-scoped Markdown assembly and table hydration.
import type {
  AssembledMarkdownContext,
  MarkdownRenderContext,
  MarkdownTextPiece,
  MarkdownRenderSnapshot,
  MarkdownPageSource,
  MarkdownValueFragment,
  MarkdownPiecePage,
  MarkdownRenderManifest,
  MarkdownValueKind,
  MarkdownPieceInterval,
} from "../MarkdownRenderModel.ts";
import {
  isIndex,
  requireMarkdownPage,
  copyMarkdownContext,
  copyMarkdownContextDescriptorPage,
  sameMarkdownContext,
  copyMarkdownPiecePage,
  sameMarkdownTextPiece,
  copyMarkdownValueFragment,
} from "./MarkdownPageValidation.ts";

export function normalizeRanges(
  ranges: readonly MarkdownPieceInterval[],
): readonly MarkdownPieceInterval[] {
  const result: MarkdownPieceInterval[] = [];
  for (const range of [...ranges].sort((a, b) => a.from - b.from)) {
    if (range.from === range.next) continue;
    const last = result.at(-1);
    if (!last || last.next < range.from) result.push(Object.freeze({ ...range }));
    else
      result[result.length - 1] = Object.freeze({
        from: last.from,
        next: Math.max(last.next, range.next),
      });
  }
  return Object.freeze(result);
}

// One read assembles a private candidate snapshot, including table support.
export class MarkdownAssemblyRead {
  readonly pieces: Map<number, MarkdownTextPiece>;
  readonly contexts: Map<number, AssembledMarkdownContext>;
  private readonly descriptors = new Map<number, MarkdownRenderContext>();
  private readonly rows = new Map<number, readonly number[]>();
  private readonly manifest: MarkdownRenderManifest;
  private readonly source: MarkdownPageSource;
  private readonly generation: string;
  private readonly signal: AbortSignal;
  private readonly available: MarkdownRenderSnapshot | undefined;

  constructor(
    source: MarkdownPageSource,
    snapshot: MarkdownRenderSnapshot,
    manifest: MarkdownRenderManifest,
    signal: AbortSignal,
    available?: MarkdownRenderSnapshot,
  ) {
    this.available = available;
    this.source = source;
    this.manifest = manifest;
    this.generation = snapshot.generation;
    this.signal = signal;
    this.pieces = new Map(snapshot.pieces);
    this.contexts = new Map(snapshot.contexts);
    for (const { context } of this.contexts.values()) this.descriptors.set(context.id, context);
    for (const { context } of available?.contexts.values() ?? [])
      if (!this.descriptors.has(context.id)) this.descriptors.set(context.id, context);
  }

  snapshotRanges(ranges: readonly MarkdownPieceInterval[]) {
    const displayRanges = normalizeRanges(ranges);
    const supportRanges = normalizeRanges(
      [...this.pieces.keys()]
        .filter((id) => !displayRanges.some(({ from, next }) => from <= id && id < next))
        .map((id) => ({ from: id, next: id + 1 })),
    );
    return { displayRanges, supportRanges };
  }

  async add(page: MarkdownPiecePage): Promise<void> {
    let last: MarkdownTextPiece | undefined;
    for (const [offset, piece] of page.pieces.entries()) {
      if (piece.id !== page.from + offset)
        throw new Error("Markdown text piece IDs are not contiguous.");
      last = piece;
      this.addPiece(piece);
    }
    const prior = this.pieces.get(page.from - 1);
    if (!page.hasMore && (last?.continued ?? prior?.continued))
      throw new Error("Markdown text piece continuation is incomplete.");

    await this.prefetchDescriptors(page.pieces.map((piece) => piece.context));
    for (const piece of page.pieces) await this.ancestry(piece.context, piece.id);
    await this.hydrateRows();
  }

  async retain(pieces: readonly MarkdownTextPiece[]): Promise<void> {
    for (const piece of pieces) {
      this.signal.throwIfAborted();
      this.addPiece(piece);
    }
    await this.prefetchDescriptors(pieces.map((piece) => piece.context));
    for (const piece of pieces) await this.ancestry(piece.context, piece.id);
    await this.hydrateRows();
  }

  private async prefetchDescriptors(seeds: readonly number[]): Promise<void> {
    let pending = [...new Set(seeds)];
    while (pending.length > 0) {
      this.signal.throwIfAborted();
      const requested = [...new Set(pending)].filter((id) => !this.descriptors.has(id));
      pending = [];
      for (const id of requested)
        if (!isIndex(id) || id >= this.manifest.contextCount)
          throw new Error("Invalid Markdown render context.");

      for (let offset = 0; offset < requested.length; offset += 32) {
        const ids = Object.freeze(requested.slice(offset, offset + 32));
        let index = 0;
        while (index < ids.length) {
          this.signal.throwIfAborted();
          const cursor = { generation: this.generation, index };
          const response = await this.source.readContexts(ids, cursor, this.signal);
          this.signal.throwIfAborted();
          const page = copyMarkdownContextDescriptorPage(response, ids, cursor, this.manifest);
          for (const context of page.contexts) {
            const existing = this.descriptors.get(context.id);
            if (existing && !sameMarkdownContext(existing, context))
              throw new Error("Markdown render context changed between descriptor pages.");
            this.descriptors.set(context.id, context);
          }
          index = page.next;
        }
      }

      for (const id of requested) {
        const context = this.descriptors.get(id);
        if (!context) throw new Error("Markdown context descriptor was not returned.");
        if (context.parent >= 0) pending.push(context.parent);
        if (context.kind === "row" && context.firstChild >= 0) pending.push(context.firstChild);
        if (context.kind === "cell") {
          if (context.nextSibling >= 0) pending.push(context.nextSibling);
          if (context.headerCell >= 0) pending.push(context.headerCell);
        }
      }
    }
  }

  private async hydrateRows(): Promise<void> {
    // Rows expose empty/padded cells and header references independently of pieces.
    for (const { context } of [...this.contexts.values()])
      if (context.kind === "row") await this.row(context.id);
  }

  private addPiece(piece: MarkdownTextPiece): void {
    if (piece.id === 0 && piece.part !== 0)
      throw new Error("Markdown render generation starts with an incomplete text piece.");
    const existing = this.pieces.get(piece.id);
    if (existing) {
      if (!sameMarkdownTextPiece(existing, piece)) throw new Error("Markdown text piece changed.");
      return;
    }
    const previous = this.pieces.get(piece.id - 1);
    const next = this.pieces.get(piece.id + 1);
    if (previous && !markdownPiecesContinue(previous, piece))
      throw new Error("Markdown text piece continuation is discontinuous.");
    if (next && !markdownPiecesContinue(piece, next))
      throw new Error("Markdown text piece continuation is discontinuous.");
    this.pieces.set(piece.id, piece);
  }

  private async context(id: number): Promise<MarkdownRenderContext> {
    const existing = this.contexts.get(id) ?? this.available?.contexts.get(id);
    if (existing) {
      this.contexts.set(id, existing);
      this.descriptors.set(id, existing.context);
      return existing.context;
    }
    if (!isIndex(id) || id >= this.manifest.contextCount)
      throw new Error("Invalid Markdown render context.");

    let descriptor = this.descriptors.get(id);
    if (descriptor?.valueCount === 0) {
      this.contexts.set(id, Object.freeze({ context: descriptor, values: Object.freeze({}) }));
      return descriptor;
    }

    let index = 0;
    let previous: MarkdownValueFragment | undefined;
    const values: Partial<Record<MarkdownValueKind, string>> = Object.create(null) as Partial<
      Record<MarkdownValueKind, string>
    >;
    while (true) {
      this.signal.throwIfAborted();
      const cursor = { generation: this.generation, index };
      const page = await this.source.readContext(id, cursor, this.signal);
      requireMarkdownPage(page, cursor, page.values);
      const receivedContext = copyMarkdownContext(page.context, id, this.manifest);
      if (descriptor && !sameMarkdownContext(descriptor, receivedContext))
        throw new Error("Markdown render context changed between pages.");
      descriptor = receivedContext;
      this.descriptors.set(id, descriptor);
      if (page.next > descriptor.valueCount || page.hasMore !== page.next < descriptor.valueCount)
        throw new Error("Markdown value count is inconsistent.");

      for (const received of page.values) {
        const value = copyMarkdownValueFragment(received);
        if (previous?.continued) {
          if (value.kind !== previous.kind || value.part !== previous.part + 1)
            throw new Error("Markdown value continuation is discontinuous.");
        } else if (value.part !== 0 || values[value.kind] !== undefined) {
          throw new Error("Unexpected Markdown value part.");
        }
        values[value.kind] = (values[value.kind] ?? "") + value.text;
        previous = value;
      }
      index = page.next;
      if (!page.hasMore) break;
    }
    if (!descriptor || previous?.continued) throw new Error("Markdown value is incomplete.");
    const assembled = Object.freeze({ context: descriptor, values: Object.freeze(values) });
    this.contexts.set(id, assembled);
    return descriptor;
  }

  private async ancestry(id: number, pieceId?: number): Promise<readonly MarkdownRenderContext[]> {
    const chain: MarkdownRenderContext[] = [];
    let context = await this.context(id);
    while (true) {
      if (
        pieceId !== undefined &&
        (pieceId < context.firstPiece || pieceId >= context.firstPiece + context.pieceCount)
      )
        throw new Error("Markdown text piece is outside its context range.");
      chain.push(context);
      if (context.parent < 0) return chain;
      context = await this.context(context.parent);
    }
  }

  private async row(id: number): Promise<readonly number[]> {
    const existing = this.rows.get(id);
    if (existing) return existing;
    const row = await this.context(id);
    if (row.kind !== "row") throw new Error("Invalid Markdown row reference.");
    const table = await this.context(row.parent);
    if (table.kind !== "table") throw new Error("Markdown row has an invalid parent.");

    const cells: number[] = [];
    let childId = row.firstChild;
    let previousId = -1;
    while (childId >= 0) {
      if (childId <= previousId) throw new Error("Invalid Markdown row children.");
      const cell = await this.context(childId);
      if (
        cell.parent !== id ||
        cell.kind !== "cell" ||
        cell.header !== row.header ||
        (cell.nextSibling >= 0 && cell.nextSibling <= cell.id)
      )
        throw new Error("Invalid Markdown row children.");
      cells.push(cell.id);
      previousId = cell.id;
      childId = cell.nextSibling;
    }
    this.rows.set(id, cells);
    for (const cellId of cells) {
      const cell = await this.context(cellId);
      if (cell.headerCell >= 0 && cell.headerCell !== cell.id)
        await this.header(cell.headerCell, table.id);
    }
    return cells;
  }

  private async header(id: number, expectedTableId: number): Promise<void> {
    const header = await this.context(id);
    if (header.kind !== "cell" || !header.header)
      throw new Error("Invalid Markdown table header context.");
    await this.ancestry(id);
    const row = await this.context(header.parent);
    if (row.kind !== "row" || !row.header) throw new Error("Invalid Markdown table header row.");
    const table = await this.context(row.parent);
    if (table.kind !== "table" || table.id !== expectedTableId)
      throw new Error("Markdown table header belongs to a different table.");
    const cells = await this.row(row.id);
    if (!cells.includes(header.id))
      throw new Error("Markdown table header is not a child of its row.");

    // Fetch header-cell piece intervals directly; body rows may separate them from the window.
    for (const cellId of cells) {
      const cell = await this.context(cellId);
      const end = cell.firstPiece + cell.pieceCount;
      let index = cell.firstPiece;
      while (index < end) {
        const existing = this.pieces.get(index) ?? this.available?.pieces.get(index);
        if (existing) {
          this.addPiece(existing);
          const ancestry = await this.ancestry(existing.context, existing.id);
          if (!ancestry.some((ancestor) => ancestor.id === cell.id))
            throw new Error("Detached header piece is outside its cell.");
          index++;
          continue;
        }
        this.signal.throwIfAborted();
        const cursor = { generation: this.generation, index };
        const page = copyMarkdownPiecePage(
          await this.source.readPieces(cursor, this.signal),
          cursor,
          this.manifest,
        );
        if (page.next <= index) throw new Error("Detached header read did not advance.");
        for (const [offset, piece] of page.pieces.entries()) {
          if (piece.id !== page.from + offset)
            throw new Error("Invalid detached Markdown text piece ID.");
          if (piece.id >= end) break;
          this.addPiece(piece);
          const ancestry = await this.ancestry(piece.context, piece.id);
          if (!ancestry.some((ancestor) => ancestor.id === cell.id))
            throw new Error("Detached header piece is outside its cell.");
        }
        index = Math.min(page.next, end);
      }
    }
  }
}

function markdownPiecesContinue(previous: MarkdownTextPiece, next: MarkdownTextPiece): boolean {
  return previous.continued
    ? next.part === previous.part + 1 &&
        next.context === previous.context &&
        next.kind === previous.kind
    : next.part === 0;
}
