import type {
  MarkdownContextPage,
  MarkdownRenderContext,
  MarkdownRenderCursor,
  MarkdownTextPiece,
  MarkdownPageSource,
  MarkdownRenderManifest,
  MarkdownValueFragment,
  MarkdownPiecePage,
} from "../markdown-rendering/MarkdownRenderModel.ts";

export interface MarkdownFixtureContext {
  readonly context: MarkdownRenderContext;
  readonly values?: readonly MarkdownValueFragment[];
}

export type MarkdownContextDescriptor = Partial<
  Omit<MarkdownRenderContext, "id" | "firstChild" | "childCount" | "firstPiece" | "pieceCount">
> & {
  readonly id?: number;
  readonly children?: readonly [firstChild: number, childCount: number];
  readonly pieces?: readonly [firstPiece: number, pieceCount: number];
  readonly values?: readonly MarkdownValueFragment[];
};

export interface MarkdownRenderFixtureOptions {
  readonly generation?: string;
  readonly piecePageSize?: number;
  readonly valuePageSize?: number;
  readonly pieceGeneration?: string;
  readonly beforePieceGeneration?: string;
  readonly contextGeneration?: string;
  readonly pieceCount?: number;
  readonly contextCount?: number;
  readonly sourceUtf8Bytes?: number;
  readonly transformPiecePage?: (page: MarkdownPiecePage) => MarkdownPiecePage;
  readonly transformBeforePiecePage?: (page: MarkdownPiecePage) => MarkdownPiecePage;
  readonly transformContextPage?: (page: MarkdownContextPage) => MarkdownContextPage;
}

export function createMarkdownContext(
  id: number,
  overrides: Partial<Omit<MarkdownRenderContext, "id">> = {},
): MarkdownRenderContext {
  return {
    id,
    kind: "paragraph",
    parent: -1,
    firstChild: -1,
    nextSibling: -1,
    childCount: 0,
    source: { start: 0, end: 0 },
    firstPiece: 0,
    pieceCount: 0,
    valueCount: 0,
    position: 0,
    level: 0,
    ordered: false,
    loose: false,
    header: false,
    alignment: "none",
    headerCell: -1,
    ...overrides,
  };
}

export function createMarkdownPiece(
  id: number,
  context: number,
  text: string,
  overrides: Partial<Omit<MarkdownTextPiece, "id" | "context" | "text">> = {},
): MarkdownTextPiece {
  return {
    id,
    context,
    kind: "text",
    text,
    source: { start: id, end: id + 1 },
    mapping: "exact",
    part: 0,
    continued: false,
    ...overrides,
  };
}

export function createMarkdownValue(
  kind: MarkdownValueFragment["kind"],
  text: string,
  part = 0,
  continued = false,
): MarkdownValueFragment {
  return { kind, text, part, continued };
}

export class SyntheticMarkdownPageSource implements MarkdownPageSource {
  readonly manifest: MarkdownRenderManifest;
  readonly pieceRequests: MarkdownRenderCursor[] = [];
  readonly beforePieceRequests: MarkdownRenderCursor[] = [];
  readonly contextRequests: { readonly id: number; readonly cursor: MarkdownRenderCursor }[] = [];

  private readonly generation: string;
  private readonly piecePageSize: number;
  private readonly valuePageSize: number;
  readonly pieces: readonly MarkdownTextPiece[];
  readonly contexts: readonly MarkdownFixtureContext[];
  private readonly options: MarkdownRenderFixtureOptions;

  constructor(
    pieces: readonly MarkdownTextPiece[],
    contexts: readonly (MarkdownFixtureContext | MarkdownContextDescriptor)[],
    options: MarkdownRenderFixtureOptions = {},
  ) {
    this.pieces = pieces;
    this.contexts = contexts.map((entry, index) => {
      if ("context" in entry) return entry;
      const { id = index, children, pieces, values, ...overrides } = entry;
      return {
        context: createMarkdownContext(id, {
          ...overrides,
          ...(children ? { firstChild: children[0], childCount: children[1] } : {}),
          ...(pieces ? { firstPiece: pieces[0], pieceCount: pieces[1] } : {}),
        }),
        ...(values === undefined ? {} : { values }),
      };
    });
    this.options = options;
    this.generation = options.generation ?? "fixture-generation";
    this.manifest = Object.freeze({
      generation: this.generation,
      sourceUtf8Bytes:
        options.sourceUtf8Bytes ??
        Math.max(
          0,
          ...pieces.map((piece) => piece.source.end),
          ...this.contexts.map((entry) => entry.context.source.end),
        ),
      pieceCount:
        options.pieceCount ?? pieces.reduce((count, piece) => Math.max(count, piece.id + 1), 0),
      contextCount:
        options.contextCount ??
        this.contexts.reduce((count, entry) => Math.max(count, entry.context.id + 1), 0),
    });
    this.piecePageSize = options.piecePageSize ?? Number.POSITIVE_INFINITY;
    this.valuePageSize = options.valuePageSize ?? Number.POSITIVE_INFINITY;
    if (this.piecePageSize <= 0 || this.valuePageSize <= 0)
      throw new RangeError("Markdown render fixture page sizes must be positive");
  }

  async readPieces(cursor: MarkdownRenderCursor, signal: AbortSignal): Promise<MarkdownPiecePage> {
    signal.throwIfAborted();
    this.pieceRequests.push({ ...cursor });
    const available = this.pieces.filter((piece) => piece.id >= cursor.index);
    const pagePieces = available.slice(0, this.piecePageSize);
    const next = cursor.index + pagePieces.length;
    const page: MarkdownPiecePage = {
      generation: this.options.pieceGeneration ?? this.generation,
      from: cursor.index,
      next,
      hasMore: next < this.manifest.pieceCount,
      pieces: pagePieces,
    };
    return this.options.transformPiecePage?.(page) ?? page;
  }

  async readPiecesBefore(
    cursor: MarkdownRenderCursor,
    signal: AbortSignal,
  ): Promise<MarkdownPiecePage> {
    signal.throwIfAborted();
    this.beforePieceRequests.push({ ...cursor });
    const available = this.pieces.filter((piece) => piece.id < cursor.index);
    const pagePieces = available.slice(-this.piecePageSize);
    const from = cursor.index - pagePieces.length;
    const page: MarkdownPiecePage = {
      generation: this.options.beforePieceGeneration ?? this.generation,
      from,
      next: cursor.index,
      hasMore: cursor.index < this.manifest.pieceCount,
      pieces: pagePieces,
    };
    return this.options.transformBeforePiecePage?.(page) ?? page;
  }

  async readContext(
    id: number,
    cursor: MarkdownRenderCursor,
    signal: AbortSignal,
  ): Promise<MarkdownContextPage> {
    signal.throwIfAborted();
    this.contextRequests.push({ id, cursor: { ...cursor } });
    const fixture = this.contexts.find((entry) => entry.context.id === id);
    if (!fixture) throw new Error(`Unknown Markdown render fixture context ${id}`);

    const values = fixture.values ?? [];
    const pageValues = values.slice(cursor.index, cursor.index + this.valuePageSize);
    const next = cursor.index + pageValues.length;
    const page: MarkdownContextPage = {
      generation: this.options.contextGeneration ?? this.generation,
      context: fixture.context,
      from: cursor.index,
      next,
      hasMore: next < values.length,
      values: pageValues,
    };
    return this.options.transformContextPage?.(page) ?? page;
  }
}
