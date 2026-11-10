// Private UI boundary, not an HTTP contract. A future adapter must normalize wire
// enums and validate responses here. IDs and cursors live for one generation only.
export type DisplayKind =
  | "document"
  | "paragraph"
  | "heading"
  | "quote"
  | "list"
  | "item"
  | "code"
  | "codeLine"
  | "table"
  | "row"
  | "cell"
  | "emphasis"
  | "strong"
  | "inlineCode"
  | "link"
  | "thematicBreak";
export type TextKind = "text" | "literal" | "code" | "softBreak" | "hardBreak" | "thematicBreak";
export type Mapping = "exact" | "containing" | "container";
export type ValueKind = "destination" | "title" | "language" | "fenceInfo" | "orderedStart";

// Half-open UTF-8 byte ranges in the original source; never JS string indices.
export interface SourceRange {
  readonly start: number;
  readonly end: number;
}
export interface DisplayCursor {
  readonly generation: string;
  readonly index: number;
}
export interface DisplayPiece {
  readonly id: number;
  readonly context: number;
  readonly kind: TextKind;
  readonly text: string;
  readonly source: SourceRange;
  readonly mapping: Mapping;
  readonly part: number;
  readonly continued: boolean;
}
export interface DisplayContext {
  readonly id: number;
  readonly kind: DisplayKind;
  readonly parent: number;
  readonly firstChild: number;
  readonly nextSibling: number;
  readonly source: SourceRange;
  readonly firstPiece: number;
  readonly pieceCount: number;
  readonly valueCount: number;
  readonly position: number;
  readonly level: number;
  readonly ordered: boolean;
  readonly loose: boolean;
  readonly header: boolean;
  readonly alignment: "none" | "left" | "center" | "right";
  readonly headerCell: number;
}
export interface DisplayValue {
  readonly kind: ValueKind;
  readonly text: string;
  readonly part: number;
  readonly continued: boolean;
}
export interface PiecePage {
  readonly generation: string;
  readonly from: number;
  readonly next: number;
  readonly hasMore: boolean;
  readonly pieces: readonly DisplayPiece[];
}
export interface ContextPage {
  readonly generation: string;
  readonly context: DisplayContext;
  readonly from: number;
  readonly next: number;
  readonly hasMore: boolean;
  readonly values: readonly DisplayValue[];
}
export interface DisplaySource {
  readPieces(cursor: DisplayCursor, signal: AbortSignal): Promise<PiecePage>;
  readContext(id: number, cursor: DisplayCursor, signal: AbortSignal): Promise<ContextPage>;
}
export interface AssembledContext {
  readonly context: DisplayContext;
  readonly values: Readonly<Partial<Record<ValueKind, string>>>;
}
export interface DisplaySnapshot {
  readonly generation: string;
  readonly from: number;
  readonly next: number;
  readonly hasMore: boolean;
  readonly contexts: ReadonlyMap<number, AssembledContext>;
  readonly pieces: ReadonlyMap<number, DisplayPiece>;
}
