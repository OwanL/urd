// Validates Markdown pages and records and makes immutable copies.
import type {
  MarkdownContextPage,
  MarkdownContextDescriptorPage,
  MarkdownRenderContext,
  MarkdownRenderCursor,
  MarkdownTextPiece,
  MarkdownValueFragment,
  MarkdownPiecePage,
  MarkdownRenderManifest,
  MarkdownValueKind,
} from "../MarkdownRenderModel.ts";

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
  "image",
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
  "imageKey",
]);
const alignments = new Set<MarkdownRenderContext["alignment"]>(["none", "left", "center", "right"]);

export function isIndex(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0;
}

export function requireMarkdownPage(
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

export function copyMarkdownContext(
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
    (context.firstValue !== undefined &&
      (!isIndex(context.firstValue) ||
        !Number.isSafeInteger(context.firstValue + context.valueCount))) ||
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

function copyMarkdownContextDescriptor(
  context: MarkdownRenderContext,
  expectedId: number,
  manifest: MarkdownRenderManifest,
): MarkdownRenderContext {
  if (context && typeof context === "object" && "values" in context)
    throw new Error("Markdown context descriptor must not include values.");
  return copyMarkdownContext(context, expectedId, manifest);
}

export function copyMarkdownContextDescriptorPage(
  page: MarkdownContextDescriptorPage,
  ids: readonly number[],
  cursor: MarkdownRenderCursor,
  manifest: MarkdownRenderManifest,
): MarkdownContextDescriptorPage {
  if (!page || typeof page !== "object" || !Array.isArray(page.contexts))
    throw new Error("Invalid Markdown context descriptor page.");
  if ("values" in page)
    throw new Error("Markdown context descriptor page must not include values.");
  if (page.generation !== cursor.generation)
    throw new Error("Markdown render generation changed; restart from the source anchor.");
  const next = page.from + page.contexts.length;
  if (
    ids.length > 256 ||
    !isIndex(cursor.index) ||
    cursor.index >= ids.length ||
    page.from !== cursor.index ||
    !Number.isSafeInteger(next) ||
    page.next !== next ||
    page.next <= cursor.index ||
    !isIndex(page.next) ||
    page.next > ids.length ||
    typeof page.hasMore !== "boolean" ||
    page.hasMore !== page.next < ids.length
  )
    throw new Error("Markdown context descriptor page did not match its request.");
  return Object.freeze({
    generation: page.generation,
    from: page.from,
    next: page.next,
    hasMore: page.hasMore,
    contexts: Object.freeze(
      page.contexts.map((context, offset) =>
        copyMarkdownContextDescriptor(context, ids[cursor.index + offset]!, manifest),
      ),
    ),
  });
}

export function sameMarkdownContext(
  left: MarkdownRenderContext,
  right: MarkdownRenderContext,
): boolean {
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
    left.firstValue === right.firstValue &&
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

export function copyMarkdownPiecePage(
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
  return copyMarkdownPiecePageRecords(page, manifest);
}

export function copyMarkdownPiecePageBefore(
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
  return copyMarkdownPiecePageRecords(page, manifest);
}

function copyMarkdownPiecePageRecords(
  page: MarkdownPiecePage,
  manifest: MarkdownRenderManifest,
): MarkdownPiecePage {
  if ("values" in page) throw new Error("Markdown piece page must not include values.");
  const { contexts } = page;
  let hints: readonly MarkdownRenderContext[] | undefined;
  if (contexts !== undefined) {
    if (!Array.isArray(contexts)) throw new Error("Invalid Markdown piece page contexts.");
    const ids = new Set<number>();
    const copies: MarkdownRenderContext[] = [];
    for (const context of contexts as readonly MarkdownRenderContext[]) {
      const descriptor = copyMarkdownContextDescriptor(context, context?.id, manifest);
      if (ids.has(descriptor.id)) throw new Error("Duplicate Markdown context in piece page.");
      ids.add(descriptor.id);
      copies.push(descriptor);
    }
    hints = Object.freeze(copies);
  }
  return Object.freeze({
    ...page,
    pieces: Object.freeze(page.pieces.map((piece) => copyMarkdownTextPiece(piece, manifest))),
    ...(hints === undefined ? {} : { contexts: hints }),
  });
}

export function copyMarkdownManifest(manifest: MarkdownRenderManifest): MarkdownRenderManifest {
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

export function sameMarkdownTextPiece(left: MarkdownTextPiece, right: MarkdownTextPiece): boolean {
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

export function copyMarkdownValueFragment(value: MarkdownValueFragment): MarkdownValueFragment {
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
