import type {
  AssembledMarkdownContext,
  MarkdownContextPage,
  MarkdownRenderContext,
  MarkdownRenderCursor,
  MarkdownTextPiece,
  MarkdownRenderSnapshot,
  MarkdownPageSource,
  MarkdownValueFragment,
  MarkdownPiecePage,
  MarkdownRenderManifest,
  MarkdownValueKind,
  MarkdownPieceInterval,
  MarkdownTrimRequest,
} from "./MarkdownRenderModel.ts";

const markdownElementKinds = new Set<MarkdownRenderContext["kind"]>([
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
const markdownTextKinds = new Set<MarkdownTextPiece["kind"]>([
  "text",
  "literal",
  "code",
  "softBreak",
  "hardBreak",
  "thematicBreak",
]);
const sourceMappings = new Set<MarkdownTextPiece["mapping"]>(["exact", "containing", "container"]);
const markdownValueKinds = new Set<MarkdownValueKind>([
  "destination",
  "title",
  "language",
  "fenceInfo",
  "orderedStart",
]);
const alignments = new Set<MarkdownRenderContext["alignment"]>(["none", "left", "center", "right"]);

function isIndex(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0;
}

function requireMarkdownPage(
  page: MarkdownPiecePage | MarkdownContextPage,
  cursor: MarkdownRenderCursor,
  records: readonly unknown[],
): void {
  if (!page || typeof page !== "object" || !Array.isArray(records))
    throw new Error("Invalid Markdown page.");
  if (page.generation !== cursor.generation)
    throw new Error("Markdown render generation changed; restart from the source anchor.");
  const next = page.from + records.length;
  if (
    page.from !== cursor.index ||
    !Number.isSafeInteger(next) ||
    page.next !== next ||
    !isIndex(page.next) ||
    typeof page.hasMore !== "boolean" ||
    (page.hasMore && page.next <= page.from)
  )
    throw new Error("Markdown page did not advance consistently.");
}

function copyMarkdownSourceRange(
  range: MarkdownRenderContext["source"],
  manifest: MarkdownRenderManifest,
): MarkdownRenderContext["source"] {
  if (
    !range ||
    !isIndex(range.start) ||
    !isIndex(range.end) ||
    range.end < range.start ||
    range.end > manifest.sourceUtf8Bytes
  )
    throw new Error("Invalid Markdown source range.");
  return Object.freeze({ start: range.start, end: range.end });
}

function copyMarkdownContext(
  context: MarkdownRenderContext,
  expectedId: number,
  manifest: MarkdownRenderManifest,
): MarkdownRenderContext {
  if (
    !context ||
    !isIndex(context.id) ||
    context.id >= manifest.contextCount ||
    context.id !== expectedId ||
    !markdownElementKinds.has(context.kind) ||
    !Number.isSafeInteger(context.parent) ||
    context.parent < -1 ||
    context.parent >= context.id ||
    !Number.isSafeInteger(context.firstChild) ||
    context.firstChild < -1 ||
    (context.firstChild >= 0 &&
      (context.firstChild <= context.id || context.firstChild >= manifest.contextCount)) ||
    !Number.isSafeInteger(context.nextSibling) ||
    context.nextSibling < -1 ||
    (context.nextSibling >= 0 &&
      (context.nextSibling <= context.id || context.nextSibling >= manifest.contextCount)) ||
    !isIndex(context.childCount) ||
    !isIndex(context.firstPiece) ||
    !isIndex(context.pieceCount) ||
    !Number.isSafeInteger(context.firstPiece + context.pieceCount) ||
    context.firstPiece + context.pieceCount > manifest.pieceCount ||
    !isIndex(context.valueCount) ||
    !isIndex(context.position) ||
    !isIndex(context.level) ||
    typeof context.ordered !== "boolean" ||
    typeof context.loose !== "boolean" ||
    typeof context.header !== "boolean" ||
    !alignments.has(context.alignment) ||
    !Number.isSafeInteger(context.headerCell) ||
    context.headerCell < -1 ||
    (context.headerCell >= 0 &&
      (context.kind !== "cell" ||
        context.headerCell > context.id ||
        context.headerCell >= manifest.contextCount)) ||
    (context.kind === "cell" &&
      (context.header ? context.headerCell !== context.id : context.headerCell === context.id))
  )
    throw new Error("Invalid Markdown render context.");
  if (
    (context.id === 0 && (context.kind !== "document" || context.parent !== -1)) ||
    (context.id > 0 && context.parent < 0)
  )
    throw new Error("Markdown render context has invalid ancestry.");
  return Object.freeze({
    ...context,
    source: copyMarkdownSourceRange(context.source, manifest),
  });
}

function sameMarkdownContext(left: MarkdownRenderContext, right: MarkdownRenderContext): boolean {
  return (
    left.id === right.id &&
    left.kind === right.kind &&
    left.parent === right.parent &&
    left.firstChild === right.firstChild &&
    left.nextSibling === right.nextSibling &&
    left.childCount === right.childCount &&
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

function copyMarkdownTextPiece(
  piece: MarkdownTextPiece,
  manifest: MarkdownRenderManifest,
): MarkdownTextPiece {
  if (
    !piece ||
    !isIndex(piece.id) ||
    piece.id >= manifest.pieceCount ||
    !isIndex(piece.context) ||
    piece.context >= manifest.contextCount ||
    !markdownTextKinds.has(piece.kind) ||
    typeof piece.text !== "string" ||
    !sourceMappings.has(piece.mapping) ||
    !isIndex(piece.part) ||
    typeof piece.continued !== "boolean"
  )
    throw new Error("Invalid Markdown text piece.");
  return Object.freeze({
    ...piece,
    source: copyMarkdownSourceRange(piece.source, manifest),
  });
}

function copyMarkdownPiecePage(
  page: MarkdownPiecePage,
  cursor: MarkdownRenderCursor,
  manifest: MarkdownRenderManifest,
): MarkdownPiecePage {
  if (!page || !Array.isArray(page.pieces)) throw new Error("Invalid Markdown page.");
  const next = page.from + page.pieces.length;
  if (page.generation !== cursor.generation)
    throw new Error("Markdown render generation changed; restart from the source anchor.");
  if (
    page.from !== cursor.index ||
    !Number.isSafeInteger(next) ||
    page.next !== next ||
    !isIndex(page.next) ||
    page.next > manifest.pieceCount ||
    typeof page.hasMore !== "boolean" ||
    page.hasMore !== page.next < manifest.pieceCount ||
    (page.hasMore && page.next <= page.from)
  )
    throw new Error("Markdown page did not advance consistently.");
  return Object.freeze({
    ...page,
    pieces: Object.freeze(page.pieces.map((piece) => copyMarkdownTextPiece(piece, manifest))),
  });
}

function copyMarkdownPiecePageBefore(
  page: MarkdownPiecePage,
  cursor: MarkdownRenderCursor,
  manifest: MarkdownRenderManifest,
): MarkdownPiecePage {
  if (!page || !Array.isArray(page.pieces)) throw new Error("Invalid Markdown page.");
  const count = page.pieces.length;
  if (page.generation !== cursor.generation)
    throw new Error("Markdown render generation changed; restart from the source anchor.");
  if (
    page.next !== cursor.index ||
    !isIndex(page.from) ||
    !isIndex(page.next) ||
    page.from > page.next ||
    page.next > manifest.pieceCount ||
    page.next - page.from !== count ||
    typeof page.hasMore !== "boolean" ||
    page.hasMore !== page.next < manifest.pieceCount ||
    (cursor.index > 0 && count === 0)
  )
    throw new Error("Markdown preceding page did not match its cursor.");
  return Object.freeze({
    ...page,
    pieces: Object.freeze(page.pieces.map((piece) => copyMarkdownTextPiece(piece, manifest))),
  });
}

function copyMarkdownManifest(manifest: MarkdownRenderManifest): MarkdownRenderManifest {
  if (
    !manifest ||
    typeof manifest.generation !== "string" ||
    manifest.generation.length === 0 ||
    !isIndex(manifest.sourceUtf8Bytes) ||
    !isIndex(manifest.pieceCount) ||
    !isIndex(manifest.contextCount)
  )
    throw new Error("Invalid Markdown render manifest.");
  return Object.freeze({ ...manifest });
}

function sameMarkdownTextPiece(left: MarkdownTextPiece, right: MarkdownTextPiece): boolean {
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

function copyMarkdownValueFragment(value: MarkdownValueFragment): MarkdownValueFragment {
  if (
    !value ||
    !markdownValueKinds.has(value.kind) ||
    typeof value.text !== "string" ||
    !isIndex(value.part) ||
    typeof value.continued !== "boolean"
  )
    throw new Error("Invalid Markdown value fragment.");
  return Object.freeze({ ...value });
}

// One assembler owns one disposable generation/window. Pages commit atomically;
// failure/abort leaves the last snapshot usable. Concurrent reads are rejected.
export class MarkdownWindowAssembler {
  private current: MarkdownRenderSnapshot;
  private reading = false;
  private readonly manifest: MarkdownRenderManifest;
  private readonly source: MarkdownPageSource;

  constructor(source: MarkdownPageSource, manifest: MarkdownRenderManifest, from = 0) {
    this.manifest = copyMarkdownManifest(manifest);
    if (!isIndex(from) || from > this.manifest.pieceCount)
      throw new Error("Invalid Markdown render window.");
    this.source = source;
    this.current = {
      generation: this.manifest.generation,
      from,
      next: from,
      hasMore: from < this.manifest.pieceCount,
      displayRanges: [],
      supportRanges: [],
      contexts: new Map(),
      pieces: new Map(),
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
      const read = new AssemblyRead(this.source, this.current, this.manifest, signal);
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
      const read = new AssemblyRead(this.source, this.current, this.manifest, signal);
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
      const read = new AssemblyRead(this.source, empty, this.manifest, signal, this.current);
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

function normalizeRanges(
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

class AssemblyRead {
  readonly pieces: Map<number, MarkdownTextPiece>;
  readonly contexts: Map<number, AssembledMarkdownContext>;
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
      await this.ancestry(piece.context, piece.id);
    }
    const prior = this.pieces.get(page.from - 1);
    if (!page.hasMore && (last?.continued ?? prior?.continued))
      throw new Error("Markdown text piece continuation is incomplete.");

    await this.hydrateRows();
  }

  async retain(pieces: readonly MarkdownTextPiece[]): Promise<void> {
    for (const piece of pieces) {
      this.signal.throwIfAborted();
      this.addPiece(piece);
      await this.ancestry(piece.context, piece.id);
    }
    await this.hydrateRows();
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
      return existing.context;
    }
    if (!isIndex(id)) throw new Error("Invalid Markdown render context.");

    let index = 0;
    let descriptor: MarkdownRenderContext | undefined;
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
