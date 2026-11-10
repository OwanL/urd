// Private Markdown rendering boundary, not an HTTP contract. A future adapter must normalize wire
// enums and validate responses here. IDs and cursors live for one generation only.
export type MarkdownElementKind =
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
export type MarkdownTextKind =
  | "text"
  | "literal"
  | "code"
  | "softBreak"
  | "hardBreak"
  | "thematicBreak";
export type MarkdownSourceMapping = "exact" | "containing" | "container";
export type MarkdownValueKind = "destination" | "title" | "language" | "fenceInfo" | "orderedStart";

// Half-open UTF-8 byte ranges in the original source; never JS string indices.
export interface MarkdownSourceRange {
  readonly start: number;
  readonly end: number;
}
export interface MarkdownRenderManifest {
  readonly generation: string;
  readonly sourceUtf8Bytes: number;
  readonly pieceCount: number;
  readonly contextCount: number;
}
export interface MarkdownRenderCursor {
  readonly generation: string;
  readonly index: number;
}
export interface MarkdownTextPiece {
  readonly id: number;
  readonly context: number;
  readonly kind: MarkdownTextKind;
  readonly text: string;
  readonly source: MarkdownSourceRange;
  readonly mapping: MarkdownSourceMapping;
  readonly part: number;
  readonly continued: boolean;
}
export interface MarkdownRenderContext {
  readonly id: number;
  readonly kind: MarkdownElementKind;
  readonly parent: number;
  readonly firstChild: number;
  readonly nextSibling: number;
  readonly childCount: number;
  readonly source: MarkdownSourceRange;
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
export interface MarkdownValueFragment {
  readonly kind: MarkdownValueKind;
  readonly text: string;
  readonly part: number;
  readonly continued: boolean;
}
export interface MarkdownPiecePage {
  readonly generation: string;
  readonly from: number;
  readonly next: number;
  readonly hasMore: boolean;
  readonly pieces: readonly MarkdownTextPiece[];
}
export interface MarkdownContextPage {
  readonly generation: string;
  readonly context: MarkdownRenderContext;
  readonly from: number;
  readonly next: number;
  readonly hasMore: boolean;
  readonly values: readonly MarkdownValueFragment[];
}
export interface MarkdownPageSource {
  readPieces(cursor: MarkdownRenderCursor, signal: AbortSignal): Promise<MarkdownPiecePage>;
  readPiecesBefore(cursor: MarkdownRenderCursor, signal: AbortSignal): Promise<MarkdownPiecePage>;
  readContext(
    id: number,
    cursor: MarkdownRenderCursor,
    signal: AbortSignal,
  ): Promise<MarkdownContextPage>;
}
export interface AssembledMarkdownContext {
  readonly context: MarkdownRenderContext;
  readonly values: Readonly<Partial<Record<MarkdownValueKind, string>>>;
}
// Half-open generation-local piece IDs, not source byte offsets.
export interface MarkdownPieceInterval {
  readonly from: number;
  readonly next: number;
}
export interface MarkdownTrimRequest {
  readonly generation: string;
  readonly active: MarkdownPieceInterval;
  readonly retain?: readonly MarkdownPieceInterval[];
}
export interface MarkdownRenderSnapshot {
  readonly generation: string;
  readonly from: number;
  readonly next: number;
  readonly hasMore: boolean;
  // Primary display islands include the active [from,next) and explicit retained pins.
  readonly displayRanges: readonly MarkdownPieceInterval[];
  // Detached dependency pieces (currently table headers), never active cursor coverage.
  readonly supportRanges: readonly MarkdownPieceInterval[];
  readonly contexts: ReadonlyMap<number, AssembledMarkdownContext>;
  readonly pieces: ReadonlyMap<number, MarkdownTextPiece>;
}
