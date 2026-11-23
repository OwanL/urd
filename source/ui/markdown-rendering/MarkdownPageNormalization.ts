import type {
  MarkdownContextDescriptorPage,
  MarkdownContextPage,
  MarkdownElementKind,
  MarkdownPiecePage,
  MarkdownRenderContext,
  MarkdownRenderManifest,
  MarkdownSourceMapping,
  MarkdownTextKind,
  MarkdownTextPiece,
  MarkdownValueFragment,
  MarkdownValueKind,
} from "./MarkdownRenderModel.ts";
import { isRecord } from "./MarkdownResponseValidation.ts";

const elementKinds = [
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
] as const satisfies readonly MarkdownElementKind[];
const textKinds = [
  "text",
  "literal",
  "code",
  "softBreak",
  "hardBreak",
  "thematicBreak",
] as const satisfies readonly MarkdownTextKind[];
const sourceMappings = [
  "exact",
  "containing",
  "container",
] as const satisfies readonly MarkdownSourceMapping[];
const valueKinds = [
  "destination",
  "title",
  "language",
  "fenceInfo",
  "orderedStart",
  "imageKey",
] as const satisfies readonly MarkdownValueKind[];
const alignments = ["none", "left", "center", "right"] as const;

export function normalizePiecePage(
  value: unknown,
  manifest: MarkdownRenderManifest,
): MarkdownPiecePage {
  if (!isRecord(value) || !Array.isArray(value.pieces))
    throw new TypeError("Server returned an invalid Markdown piece page.");
  requireGeneration(value.generation, manifest);
  const { contexts } = value;
  if (contexts !== undefined && !Array.isArray(contexts))
    throw new TypeError("Server returned invalid Markdown piece page contexts.");
  return {
    ...value,
    pieces: value.pieces.map((piece) => normalizeTextPiece(piece)),
    ...(contexts === undefined ? {} : { contexts: contexts.map(normalizeContext) }),
  } as unknown as MarkdownPiecePage;
}

export function normalizeContextPage(
  value: unknown,
  manifest: MarkdownRenderManifest,
): MarkdownContextPage {
  if (!isRecord(value) || !Array.isArray(value.values))
    throw new TypeError("Server returned an invalid Markdown context page.");
  requireGeneration(value.generation, manifest);
  return {
    ...value,
    context: normalizeContext(value.context),
    values: value.values.map((fragment) => normalizeValue(fragment)),
  } as unknown as MarkdownContextPage;
}

export function normalizeContextDescriptorPage(
  value: unknown,
  manifest: MarkdownRenderManifest,
): MarkdownContextDescriptorPage {
  if (!isRecord(value) || !Array.isArray(value.contexts))
    throw new TypeError("Server returned an invalid Markdown context descriptor page.");
  requireGeneration(value.generation, manifest);
  return {
    ...value,
    contexts: value.contexts.map((context) => normalizeContext(context)),
  } as unknown as MarkdownContextDescriptorPage;
}

function normalizeTextPiece(value: unknown): MarkdownTextPiece {
  if (!isRecord(value)) throw new TypeError("Server returned an invalid Markdown text piece.");
  return {
    ...value,
    kind: enumAt(value.kind, textKinds, "text kind"),
    mapping: enumAt(value.mapping, sourceMappings, "source mapping"),
  } as unknown as MarkdownTextPiece;
}

function normalizeContext(value: unknown): MarkdownRenderContext {
  if (!isRecord(value)) throw new TypeError("Server returned an invalid Markdown render context.");
  return {
    ...value,
    kind: enumAt(value.kind, elementKinds, "context kind"),
    alignment: enumAt(value.alignment, alignments, "table alignment"),
  } as unknown as MarkdownRenderContext;
}

function normalizeValue(value: unknown): MarkdownValueFragment {
  if (!isRecord(value)) throw new TypeError("Server returned an invalid Markdown value fragment.");
  return {
    ...value,
    kind: enumAt(value.kind, valueKinds, "value kind"),
  } as unknown as MarkdownValueFragment;
}

function requireGeneration(value: unknown, manifest: MarkdownRenderManifest): void {
  if (value !== manifest.generation)
    throw new Error("Markdown render generation changed; restart from the source anchor.");
}

function enumAt<T extends string>(value: unknown, values: readonly T[], name: string): T {
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    value < 0 ||
    value >= values.length
  )
    throw new TypeError(`Server returned an unknown Markdown ${name} index.`);
  return values[value]!;
}
