import { RequestError } from "../http/RequestError.ts";
import { requestEmpty, requestJson } from "../http/requestJson.ts";
import type {
  MessageContentCursor,
  TranscriptEntryRef,
  TranscriptTextFacts,
} from "../sessions/Messages.ts";
import type {
  MarkdownContextPage,
  MarkdownContextDescriptorPage,
  MarkdownElementKind,
  MarkdownGenerationRequest,
  MarkdownOwnedGeneration,
  MarkdownPiecePage,
  MarkdownRenderContext,
  MarkdownRenderCursor,
  MarkdownRenderManifest,
  MarkdownSourceMapping,
  MarkdownTextKind,
  MarkdownTextPiece,
  MarkdownValueFragment,
  MarkdownValueKind,
} from "./MarkdownRenderModel.ts";
import { copyMarkdownManifest } from "./MarkdownWindowAssembler.ts";

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
] as const satisfies readonly MarkdownValueKind[];
const alignments = ["none", "left", "center", "right"] as const;
const guidPattern = /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/iu;

// Source facts are exposed for the later transcript integration; the renderer uses the paged API.
export interface MarkdownOwnedTextGeneration extends MarkdownOwnedGeneration {
  readonly source: TranscriptTextFacts;
  sourceCursor(sourceUtf8Offset: number, signal: AbortSignal): Promise<MessageContentCursor>;
  locateSource(cursor: MessageContentCursor, signal: AbortSignal): Promise<MarkdownRenderCursor>;
}

export interface MarkdownClientRequest extends MarkdownGenerationRequest {
  open(signal: AbortSignal): Promise<MarkdownOwnedTextGeneration>;
}

export class MarkdownClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  request(streamId: string, sessionId: string, entry: TranscriptEntryRef): MarkdownClientRequest {
    return {
      open: (signal) => this.open(streamId, sessionId, entry, signal),
    };
  }

  private async open(
    streamId: string,
    sessionId: string,
    entry: TranscriptEntryRef,
    signal: AbortSignal,
  ): Promise<MarkdownOwnedTextGeneration> {
    const leaseId = crypto.randomUUID();
    const base = this.url(streamId, leaseId);
    try {
      signal.throwIfAborted();
      const response = await requestJson<unknown>(base, "POST", { sessionId, entry }, signal);
      signal.throwIfAborted();
      const opened = copyOpenResponse(response);
      if (opened.manifest.generation.toLowerCase() === leaseId.toLowerCase())
        throw new TypeError("Markdown lease and render generation must be distinct.");
      return new MarkdownHttpGeneration(base, opened.manifest, opened.source);
    } catch (error) {
      try {
        await releaseLease(base);
      } catch (releaseError) {
        throw new AggregateError(
          [error, releaseError],
          "Markdown open failed and lease cleanup failed.",
        );
      }
      throw error;
    }
  }

  private url(streamId: string, leaseId: string, path = ""): URL {
    return new URL(
      `/api/events/${encodeURIComponent(streamId)}/markdown/${encodeURIComponent(leaseId)}${path}`,
      this.origin,
    );
  }
}

class MarkdownHttpGeneration implements MarkdownOwnedTextGeneration {
  private readonly base: URL;
  private disposed = false;
  private release: Promise<void> | null = null;
  readonly manifest: MarkdownRenderManifest;
  readonly source: TranscriptTextFacts;

  constructor(base: URL, manifest: MarkdownRenderManifest, source: TranscriptTextFacts) {
    this.base = base;
    this.manifest = manifest;
    this.source = source;
  }

  async locate(sourceUtf8Offset: number, signal: AbortSignal): Promise<MarkdownRenderCursor> {
    this.assertReadable();
    if (!isIndex(sourceUtf8Offset) || sourceUtf8Offset > this.manifest.sourceUtf8Bytes)
      throw new RangeError("Invalid Markdown source offset.");
    const url = this.path("/locate");
    url.searchParams.set("sourceUtf8Offset", String(sourceUtf8Offset));
    const cursor = await requestJson<unknown>(url, "GET", undefined, signal);
    return copyCursor(cursor, this.manifest, this.manifest.pieceCount, "located cursor");
  }

  async locateChild(
    contextId: number,
    childIndex: number,
    signal: AbortSignal,
  ): Promise<MarkdownRenderCursor> {
    this.assertReadable();
    this.assertContextId(contextId);
    if (!isIndex(childIndex)) throw new RangeError("Invalid Markdown child index.");
    const cursor = await requestJson<unknown>(
      this.path(`/contexts/${contextId}/children/${childIndex}`),
      "GET",
      undefined,
      signal,
    );
    return copyCursor(
      cursor,
      this.manifest,
      this.manifest.pieceCount,
      "located child cursor",
      true,
    );
  }

  async sourceCursor(sourceUtf8Offset: number, signal: AbortSignal): Promise<MessageContentCursor> {
    this.assertReadable();
    if (!isIndex(sourceUtf8Offset) || sourceUtf8Offset > this.manifest.sourceUtf8Bytes)
      throw new RangeError("Invalid Markdown source offset.");
    const url = this.path("/source");
    url.searchParams.set("sourceUtf8Offset", String(sourceUtf8Offset));
    return copyMessageCursor(await requestJson<unknown>(url, "GET", undefined, signal));
  }

  async locateSource(
    cursor: MessageContentCursor,
    signal: AbortSignal,
  ): Promise<MarkdownRenderCursor> {
    this.assertReadable();
    const location = await requestJson<unknown>(
      this.path("/locate"),
      "POST",
      copyMessageCursor(cursor),
      signal,
    );
    return copyCursor(location, this.manifest, this.manifest.pieceCount, "located source cursor");
  }

  async readPieces(cursor: MarkdownRenderCursor, signal: AbortSignal): Promise<MarkdownPiecePage> {
    this.assertCursor(cursor, this.manifest.pieceCount);
    return normalizePiecePage(
      await requestJson<unknown>(this.pageUrl("/pieces", cursor), "GET", undefined, signal),
      this.manifest,
    );
  }

  async readPiecesBefore(
    cursor: MarkdownRenderCursor,
    signal: AbortSignal,
  ): Promise<MarkdownPiecePage> {
    this.assertCursor(cursor, this.manifest.pieceCount);
    return normalizePiecePage(
      await requestJson<unknown>(this.pageUrl("/pieces/before", cursor), "GET", undefined, signal),
      this.manifest,
    );
  }

  async readContext(
    id: number,
    cursor: MarkdownRenderCursor,
    signal: AbortSignal,
  ): Promise<MarkdownContextPage> {
    this.assertReadable();
    this.assertContextId(id);
    this.assertCursor(cursor, Number.MAX_SAFE_INTEGER);
    const page = normalizeContextPage(
      await requestJson<unknown>(this.pageUrl(`/contexts/${id}`, cursor), "GET", undefined, signal),
      this.manifest,
    );
    if (!isIndex(page.context.valueCount) || cursor.index > page.context.valueCount)
      throw new RangeError("Unknown Markdown context value index.");
    return page;
  }

  async readContexts(
    ids: readonly number[],
    cursor: MarkdownRenderCursor,
    signal: AbortSignal,
  ): Promise<MarkdownContextDescriptorPage> {
    this.assertReadable();
    if (!Array.isArray(ids) || ids.length === 0 || ids.length > 256)
      throw new RangeError("Invalid Markdown context descriptor request.");
    const distinct = new Set<number>();
    for (const id of ids) {
      this.assertContextId(id);
      if (distinct.has(id)) throw new RangeError("Markdown context IDs must be distinct.");
      distinct.add(id);
    }
    this.assertCursor(cursor, ids.length - 1);
    const page = await requestJson<unknown>(
      this.path("/contexts"),
      "POST",
      {
        generation: cursor.generation,
        index: cursor.index,
        ids,
      },
      signal,
    );
    return normalizeContextDescriptorPage(page, this.manifest);
  }

  dispose(): Promise<void> {
    if (this.release) return this.release;
    this.disposed = true;
    this.release = releaseLease(this.base);
    return this.release;
  }

  private assertReadable(): void {
    if (this.disposed) throw new Error("Markdown source has been disposed.");
  }

  private assertContextId(id: number): void {
    if (!isIndex(id) || id >= this.manifest.contextCount)
      throw new RangeError("Unknown Markdown context ID.");
  }

  private assertCursor(cursor: MarkdownRenderCursor, maximum: number): void {
    this.assertReadable();
    if (!cursor || cursor.generation !== this.manifest.generation)
      throw new Error("Markdown render generation changed; restart from the source anchor.");
    if (!isIndex(cursor.index) || cursor.index > maximum)
      throw new RangeError("Unknown Markdown render index.");
  }

  private pageUrl(path: string, cursor: MarkdownRenderCursor): URL {
    const url = this.path(path);
    url.searchParams.set("generation", cursor.generation);
    url.searchParams.set("index", String(cursor.index));
    return url;
  }

  private path(path: string): URL {
    return new URL(`${this.base.pathname}${path}`, this.base.origin);
  }
}

function releaseLease(base: URL): Promise<void> {
  return requestEmpty(new URL(`${base.pathname}/release`, base.origin), "POST", {}).catch(
    (error: unknown) => {
      if (error instanceof RequestError && (error.status === 404 || error.status === 410)) return;
      throw error;
    },
  );
}

function copyOpenResponse(value: unknown): {
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

function copyMessageCursor(value: unknown): MessageContentCursor {
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

function copyCursor(
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

function normalizePiecePage(value: unknown, manifest: MarkdownRenderManifest): MarkdownPiecePage {
  if (!isRecord(value) || !Array.isArray(value.pieces))
    throw new TypeError("Server returned an invalid Markdown piece page.");
  requireGeneration(value.generation, manifest);
  return {
    ...value,
    pieces: value.pieces.map((piece) => normalizeTextPiece(piece)),
  } as unknown as MarkdownPiecePage;
}

function normalizeContextPage(
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

function normalizeContextDescriptorPage(
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

function isIndex(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
