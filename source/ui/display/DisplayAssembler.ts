import type {
  AssembledContext,
  ContextPage,
  DisplayContext,
  DisplayCursor,
  DisplayPiece,
  DisplaySnapshot,
  DisplaySource,
  DisplayValue,
  PiecePage,
  ValueKind,
} from "./DisplayModel.ts";

const displayKinds = new Set<DisplayContext["kind"]>([
  "document",
  "paragraph",
  "heading",
  "quote",
  "list",
  "item",
  "code",
  "codeLine",
  "table",
  "row",
  "cell",
  "emphasis",
  "strong",
  "inlineCode",
  "link",
  "thematicBreak",
]);
const textKinds = new Set<DisplayPiece["kind"]>([
  "text",
  "literal",
  "code",
  "softBreak",
  "hardBreak",
  "thematicBreak",
]);
const mappings = new Set<DisplayPiece["mapping"]>(["exact", "containing", "container"]);
const valueKinds = new Set<ValueKind>([
  "destination",
  "title",
  "language",
  "fenceInfo",
  "orderedStart",
]);
const alignments = new Set<DisplayContext["alignment"]>(["none", "left", "center", "right"]);

function isIndex(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0;
}

function requirePage(
  page: PiecePage | ContextPage,
  cursor: DisplayCursor,
  records: readonly unknown[],
): void {
  if (!page || typeof page !== "object" || !Array.isArray(records))
    throw new Error("Invalid display page.");
  if (page.generation !== cursor.generation)
    throw new Error("Display generation changed; restart from the source anchor.");
  const next = page.from + records.length;
  if (
    page.from !== cursor.index ||
    !Number.isSafeInteger(next) ||
    page.next !== next ||
    !isIndex(page.next) ||
    typeof page.hasMore !== "boolean" ||
    (page.hasMore && page.next <= page.from)
  )
    throw new Error("Display page did not advance consistently.");
}

function copyRange(range: DisplayContext["source"]): DisplayContext["source"] {
  if (!range || !isIndex(range.start) || !isIndex(range.end) || range.end < range.start)
    throw new Error("Invalid display source range.");
  return Object.freeze({ start: range.start, end: range.end });
}

function copyContext(context: DisplayContext, expectedId: number): DisplayContext {
  if (
    !context ||
    !isIndex(context.id) ||
    context.id !== expectedId ||
    !displayKinds.has(context.kind) ||
    !Number.isSafeInteger(context.parent) ||
    context.parent < -1 ||
    context.parent >= context.id ||
    !Number.isSafeInteger(context.firstChild) ||
    context.firstChild < -1 ||
    (context.firstChild >= 0 && context.firstChild <= context.id) ||
    !Number.isSafeInteger(context.nextSibling) ||
    context.nextSibling < -1 ||
    (context.nextSibling >= 0 && context.nextSibling <= context.id) ||
    !isIndex(context.firstPiece) ||
    !isIndex(context.pieceCount) ||
    !Number.isSafeInteger(context.firstPiece + context.pieceCount) ||
    !isIndex(context.valueCount) ||
    !isIndex(context.position) ||
    !isIndex(context.level) ||
    typeof context.ordered !== "boolean" ||
    typeof context.loose !== "boolean" ||
    typeof context.header !== "boolean" ||
    !alignments.has(context.alignment) ||
    !Number.isSafeInteger(context.headerCell) ||
    context.headerCell < -1 ||
    (context.headerCell >= 0 && (context.kind !== "cell" || context.headerCell > context.id)) ||
    (context.kind === "cell" &&
      (context.header ? context.headerCell !== context.id : context.headerCell === context.id))
  )
    throw new Error("Invalid display context.");
  if (
    (context.id === 0 && (context.kind !== "document" || context.parent !== -1)) ||
    (context.id > 0 && context.parent < 0)
  )
    throw new Error("Display context has invalid ancestry.");
  return Object.freeze({ ...context, source: copyRange(context.source) });
}

function sameContext(left: DisplayContext, right: DisplayContext): boolean {
  return (
    left.id === right.id &&
    left.kind === right.kind &&
    left.parent === right.parent &&
    left.firstChild === right.firstChild &&
    left.nextSibling === right.nextSibling &&
    left.source.start === right.source.start &&
    left.source.end === right.source.end &&
    left.firstPiece === right.firstPiece &&
    left.pieceCount === right.pieceCount &&
    left.valueCount === right.valueCount &&
    left.position === right.position &&
    left.level === right.level &&
    left.ordered === right.ordered &&
    left.loose === right.loose &&
    left.header === right.header &&
    left.alignment === right.alignment &&
    left.headerCell === right.headerCell
  );
}

function copyPiece(piece: DisplayPiece): DisplayPiece {
  if (
    !piece ||
    !isIndex(piece.id) ||
    !isIndex(piece.context) ||
    !textKinds.has(piece.kind) ||
    typeof piece.text !== "string" ||
    !mappings.has(piece.mapping) ||
    !isIndex(piece.part) ||
    typeof piece.continued !== "boolean"
  )
    throw new Error("Invalid display piece.");
  return Object.freeze({ ...piece, source: copyRange(piece.source) });
}

function copyPiecePage(page: PiecePage, cursor: DisplayCursor): PiecePage {
  requirePage(page, cursor, page.pieces);
  return Object.freeze({ ...page, pieces: Object.freeze(page.pieces.map(copyPiece)) });
}

function samePiece(left: DisplayPiece, right: DisplayPiece): boolean {
  return (
    left.id === right.id &&
    left.context === right.context &&
    left.kind === right.kind &&
    left.text === right.text &&
    left.source.start === right.source.start &&
    left.source.end === right.source.end &&
    left.mapping === right.mapping &&
    left.part === right.part &&
    left.continued === right.continued
  );
}

function copyValue(value: DisplayValue): DisplayValue {
  if (
    !value ||
    !valueKinds.has(value.kind) ||
    typeof value.text !== "string" ||
    !isIndex(value.part) ||
    typeof value.continued !== "boolean"
  )
    throw new Error("Invalid display value.");
  return Object.freeze({ ...value });
}

// One assembler owns one disposable generation/window. Pages commit atomically;
// failure/abort leaves the last snapshot usable. Concurrent appends are rejected.
export class DisplayAssembler {
  private current: DisplaySnapshot;
  private reading = false;
  private readonly source: DisplaySource;

  constructor(source: DisplaySource, generation: string, from = 0) {
    if (!generation || !isIndex(from)) throw new Error("Invalid display window.");
    this.source = source;
    this.current = {
      generation,
      from,
      next: from,
      hasMore: true,
      contexts: new Map(),
      pieces: new Map(),
    };
  }

  get snapshot(): DisplaySnapshot {
    return this.current;
  }

  async append(signal: AbortSignal): Promise<DisplaySnapshot> {
    if (this.reading) throw new Error("A display read is already pending.");
    if (!this.current.hasMore) return this.current;
    signal.throwIfAborted();
    this.reading = true;
    try {
      const read = new AssemblyRead(this.source, this.current, signal);
      const cursor = { generation: this.current.generation, index: this.current.next };
      const page = copyPiecePage(await this.source.readPieces(cursor, signal), cursor);
      await read.add(page);
      signal.throwIfAborted();
      this.current = {
        ...this.current,
        next: page.next,
        hasMore: page.hasMore,
        pieces: read.pieces,
        contexts: read.contexts,
      };
      return this.current;
    } finally {
      this.reading = false;
    }
  }
}

class AssemblyRead {
  readonly pieces: Map<number, DisplayPiece>;
  readonly contexts: Map<number, AssembledContext>;
  private readonly rows = new Map<number, readonly number[]>();
  private readonly source: DisplaySource;
  private readonly generation: string;
  private readonly signal: AbortSignal;

  constructor(source: DisplaySource, snapshot: DisplaySnapshot, signal: AbortSignal) {
    this.source = source;
    this.generation = snapshot.generation;
    this.signal = signal;
    this.pieces = new Map(snapshot.pieces);
    this.contexts = new Map(snapshot.contexts);
  }

  async add(page: PiecePage): Promise<void> {
    let last: DisplayPiece | undefined;
    for (const [offset, piece] of page.pieces.entries()) {
      if (piece.id !== page.from + offset) throw new Error("Display piece IDs are not contiguous.");
      last = piece;
      this.addPiece(piece);
      await this.ancestry(piece.context, piece.id);
    }
    const prior = this.pieces.get(page.from - 1);
    if (!page.hasMore && (last?.continued ?? prior?.continued))
      throw new Error("Display piece continuation is incomplete.");

    // Rows expose empty/padded cells and header references independently of pieces.
    for (const { context } of [...this.contexts.values()])
      if (context.kind === "row") await this.row(context.id);
  }

  private addPiece(piece: DisplayPiece): void {
    if (piece.id === 0 && piece.part !== 0)
      throw new Error("Display generation starts with an incomplete piece.");
    const existing = this.pieces.get(piece.id);
    if (existing) {
      if (!samePiece(existing, piece)) throw new Error("Display piece changed.");
      return;
    }
    const previous = this.pieces.get(piece.id - 1);
    const next = this.pieces.get(piece.id + 1);
    if (previous && !continues(previous, piece))
      throw new Error("Display piece continuation is discontinuous.");
    if (next && !continues(piece, next))
      throw new Error("Display piece continuation is discontinuous.");
    this.pieces.set(piece.id, piece);
  }

  private async context(id: number): Promise<DisplayContext> {
    const existing = this.contexts.get(id);
    if (existing) return existing.context;
    if (!isIndex(id)) throw new Error("Invalid display context.");

    let index = 0;
    let descriptor: DisplayContext | undefined;
    let previous: DisplayValue | undefined;
    const values: Partial<Record<ValueKind, string>> = Object.create(null) as Partial<
      Record<ValueKind, string>
    >;
    while (true) {
      this.signal.throwIfAborted();
      const cursor = { generation: this.generation, index };
      const page = await this.source.readContext(id, cursor, this.signal);
      requirePage(page, cursor, page.values);
      const receivedContext = copyContext(page.context, id);
      if (descriptor && !sameContext(descriptor, receivedContext))
        throw new Error("Display context changed between pages.");
      descriptor = receivedContext;
      if (page.next > descriptor.valueCount || page.hasMore !== page.next < descriptor.valueCount)
        throw new Error("Display value count is inconsistent.");

      for (const received of page.values) {
        const value = copyValue(received);
        if (previous?.continued) {
          if (value.kind !== previous.kind || value.part !== previous.part + 1)
            throw new Error("Display value continuation is discontinuous.");
        } else if (value.part !== 0 || values[value.kind] !== undefined) {
          throw new Error("Unexpected display value part.");
        }
        values[value.kind] = (values[value.kind] ?? "") + value.text;
        previous = value;
      }
      index = page.next;
      if (!page.hasMore) break;
    }
    if (!descriptor || previous?.continued) throw new Error("Display value is incomplete.");
    const assembled = Object.freeze({ context: descriptor, values: Object.freeze(values) });
    this.contexts.set(id, assembled);
    return descriptor;
  }

  private async ancestry(id: number, pieceId?: number): Promise<readonly DisplayContext[]> {
    const chain: DisplayContext[] = [];
    let context = await this.context(id);
    while (true) {
      if (
        pieceId !== undefined &&
        (pieceId < context.firstPiece || pieceId >= context.firstPiece + context.pieceCount)
      )
        throw new Error("Display piece is outside its context range.");
      chain.push(context);
      if (context.parent < 0) return chain;
      context = await this.context(context.parent);
    }
  }

  private async row(id: number): Promise<readonly number[]> {
    const existing = this.rows.get(id);
    if (existing) return existing;
    const row = await this.context(id);
    if (row.kind !== "row") throw new Error("Invalid display row reference.");
    const table = await this.context(row.parent);
    if (table.kind !== "table") throw new Error("Display row has an invalid parent.");

    const cells: number[] = [];
    let childId = row.firstChild;
    let previousId = -1;
    while (childId >= 0) {
      if (childId <= previousId) throw new Error("Invalid display row children.");
      const cell = await this.context(childId);
      if (
        cell.parent !== id ||
        cell.kind !== "cell" ||
        cell.header !== row.header ||
        (cell.nextSibling >= 0 && cell.nextSibling <= cell.id)
      )
        throw new Error("Invalid display row children.");
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
    if (header.kind !== "cell" || !header.header) throw new Error("Invalid table header context.");
    await this.ancestry(id);
    const row = await this.context(header.parent);
    if (row.kind !== "row" || !row.header) throw new Error("Invalid table header row.");
    const table = await this.context(row.parent);
    if (table.kind !== "table" || table.id !== expectedTableId)
      throw new Error("Table header belongs to a different table.");
    const cells = await this.row(row.id);
    if (!cells.includes(header.id)) throw new Error("Table header is not a child of its row.");

    // Fetch header-cell piece intervals directly; body rows may separate them from the window.
    for (const cellId of cells) {
      const cell = await this.context(cellId);
      const end = cell.firstPiece + cell.pieceCount;
      let index = cell.firstPiece;
      while (index < end) {
        const existing = this.pieces.get(index);
        if (existing) {
          const ancestry = await this.ancestry(existing.context, existing.id);
          if (!ancestry.some((ancestor) => ancestor.id === cell.id))
            throw new Error("Detached header piece is outside its cell.");
          index++;
          continue;
        }
        this.signal.throwIfAborted();
        const cursor = { generation: this.generation, index };
        const page = copyPiecePage(await this.source.readPieces(cursor, this.signal), cursor);
        if (page.next <= index) throw new Error("Detached header read did not advance.");
        for (const [offset, piece] of page.pieces.entries()) {
          if (piece.id !== page.from + offset) throw new Error("Invalid detached piece ID.");
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

function continues(previous: DisplayPiece, next: DisplayPiece): boolean {
  return previous.continued
    ? next.part === previous.part + 1 &&
        next.context === previous.context &&
        next.kind === previous.kind
    : next.part === 0;
}
