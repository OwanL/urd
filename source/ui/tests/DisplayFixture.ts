import type {
  ContextPage,
  DisplayContext,
  DisplayCursor,
  DisplayPiece,
  DisplaySource,
  DisplayValue,
  PiecePage,
} from "../display/DisplayModel.ts";

export interface FixtureContext {
  readonly context: DisplayContext;
  readonly values?: readonly DisplayValue[];
}

export interface DisplayFixtureOptions {
  readonly generation?: string;
  readonly piecePageSize?: number;
  readonly valuePageSize?: number;
  readonly pieceGeneration?: string;
  readonly contextGeneration?: string;
  readonly transformPiecePage?: (page: PiecePage) => PiecePage;
  readonly transformContextPage?: (page: ContextPage) => ContextPage;
}

export function createDisplayContext(
  id: number,
  overrides: Partial<Omit<DisplayContext, "id">> = {},
): DisplayContext {
  return {
    id,
    kind: "paragraph",
    parent: -1,
    firstChild: -1,
    nextSibling: -1,
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

export function createDisplayPiece(
  id: number,
  context: number,
  text: string,
  overrides: Partial<Omit<DisplayPiece, "id" | "context" | "text">> = {},
): DisplayPiece {
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

export function createDisplayValue(
  kind: DisplayValue["kind"],
  text: string,
  part = 0,
  continued = false,
): DisplayValue {
  return { kind, text, part, continued };
}

export class SyntheticDisplaySource implements DisplaySource {
  readonly pieceRequests: DisplayCursor[] = [];
  readonly contextRequests: { readonly id: number; readonly cursor: DisplayCursor }[] = [];

  private readonly generation: string;
  private readonly piecePageSize: number;
  private readonly valuePageSize: number;
  readonly pieces: readonly DisplayPiece[];
  readonly contexts: readonly FixtureContext[];
  private readonly options: DisplayFixtureOptions;

  constructor(
    pieces: readonly DisplayPiece[],
    contexts: readonly FixtureContext[],
    options: DisplayFixtureOptions = {},
  ) {
    this.pieces = pieces;
    this.contexts = contexts;
    this.options = options;
    this.generation = options.generation ?? "fixture-generation";
    this.piecePageSize = options.piecePageSize ?? Number.POSITIVE_INFINITY;
    this.valuePageSize = options.valuePageSize ?? Number.POSITIVE_INFINITY;
    if (this.piecePageSize <= 0 || this.valuePageSize <= 0)
      throw new RangeError("Fixture page sizes must be positive");
  }

  async readPieces(cursor: DisplayCursor, signal: AbortSignal): Promise<PiecePage> {
    signal.throwIfAborted();
    this.pieceRequests.push({ ...cursor });
    const available = this.pieces.filter((piece) => piece.id >= cursor.index);
    const pagePieces = available.slice(0, this.piecePageSize);
    const next = pagePieces.length ? pagePieces.at(-1)!.id + 1 : cursor.index;
    const page: PiecePage = {
      generation: this.options.pieceGeneration ?? this.generation,
      from: cursor.index,
      next,
      hasMore: available.length > pagePieces.length,
      pieces: pagePieces,
    };
    return this.options.transformPiecePage?.(page) ?? page;
  }

  async readContext(id: number, cursor: DisplayCursor, signal: AbortSignal): Promise<ContextPage> {
    signal.throwIfAborted();
    this.contextRequests.push({ id, cursor: { ...cursor } });
    const fixture = this.contexts.find((entry) => entry.context.id === id);
    if (!fixture) throw new Error(`Unknown fixture context ${id}`);

    const values = fixture.values ?? [];
    const pageValues = values.slice(cursor.index, cursor.index + this.valuePageSize);
    const next = cursor.index + pageValues.length;
    const page: ContextPage = {
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
