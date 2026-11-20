import type { MessageContentCursor, TranscriptTextFacts } from "../sessions/transcript/Messages.ts";
import type { MarkdownRenderCursor, MarkdownRenderManifest } from "./MarkdownRenderModel.ts";
import { copyMarkdownManifest } from "./windowing/MarkdownPageValidation.ts";

const guidPattern = /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/iu;

export function copyOpenResponse(value: unknown): {
  readonly manifest: MarkdownRenderManifest;
  readonly source: TranscriptTextFacts;
} {
  if (!isRecord(value)) throw new TypeError("Server returned an invalid Markdown open response.");
  const manifest = copyMarkdownManifest(value.manifest as MarkdownRenderManifest);
  if (!guidPattern.test(manifest.generation))
    throw new TypeError("Server returned an invalid Markdown generation identity.");
  return { manifest, source: copyTextFacts(value.source) };
}

function copyTextFacts(value: unknown): TranscriptTextFacts {
  if (
    !isRecord(value) ||
    typeof value.isClosed !== "boolean" ||
    typeof value.finalOutputCommitted !== "boolean"
  )
    throw new TypeError("Server returned invalid Markdown source facts.");
  const from = copyMessageCursor(value.from);
  const availableThrough = copyMessageCursor(value.availableThrough);
  if (
    from.message.executionId !== availableThrough.message.executionId ||
    from.message.messageId !== availableThrough.message.messageId ||
    from.message.historyRevision !== availableThrough.message.historyRevision ||
    from.message.role !== availableThrough.message.role ||
    compareCursor(availableThrough, from) < 0
  )
    throw new TypeError("Server returned inconsistent Markdown source cursors.");
  return Object.freeze({
    from,
    availableThrough,
    isClosed: value.isClosed,
    finalOutputCommitted: value.finalOutputCommitted,
  });
}

export function copyMessageCursor(value: unknown): MessageContentCursor {
  if (!isRecord(value) || !isRecord(value.message))
    throw new TypeError("Server returned an invalid Markdown source cursor.");
  const message = value.message;
  if (
    typeof message.executionId !== "string" ||
    !guidPattern.test(message.executionId) ||
    typeof message.messageId !== "string" ||
    !guidPattern.test(message.messageId) ||
    (message.role !== "user" && message.role !== "assistant") ||
    !isIndex(message.historyRevision) ||
    !isIndex(value.segmentIndex) ||
    !isIndex(value.utf8Offset)
  )
    throw new TypeError("Server returned an invalid Markdown source cursor.");
  return Object.freeze({
    message: Object.freeze({
      executionId: message.executionId,
      messageId: message.messageId,
      role: message.role,
      historyRevision: message.historyRevision,
    }),
    segmentIndex: value.segmentIndex,
    utf8Offset: value.utf8Offset,
  });
}

function compareCursor(left: MessageContentCursor, right: MessageContentCursor): number {
  return left.segmentIndex - right.segmentIndex || left.utf8Offset - right.utf8Offset;
}

export function copyCursor(
  value: unknown,
  manifest: MarkdownRenderManifest,
  maximum: number,
  name: string,
  allowEnd = false,
): MarkdownRenderCursor {
  if (
    !isRecord(value) ||
    value.generation !== manifest.generation ||
    !isIndex(value.index) ||
    value.index > maximum ||
    (manifest.pieceCount === 0 && value.index !== 0) ||
    (!allowEnd && manifest.pieceCount > 0 && value.index === manifest.pieceCount)
  )
    throw new TypeError(`Server returned an invalid Markdown ${name}.`);
  return { generation: manifest.generation, index: value.index };
}

export function isIndex(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
