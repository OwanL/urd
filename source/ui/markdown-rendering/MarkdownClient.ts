import { RequestError } from "../http/RequestError.ts";
import { requestBlob, requestEmpty, requestJson } from "../http/Requests.ts";
import type {
  MessageContentCursor,
  TranscriptEntryRef,
  TranscriptTextFacts,
} from "../sessions/transcript/Messages.ts";
import type {
  MarkdownContextPage,
  MarkdownContextDescriptorPage,
  MarkdownGenerationRequest,
  MarkdownImageLoader,
  MarkdownOwnedGeneration,
  MarkdownPiecePage,
  MarkdownRenderCursor,
  MarkdownRenderManifest,
} from "./MarkdownRenderModel.ts";
import { isMarkdownImageKey } from "./MarkdownRenderModel.ts";
import {
  normalizeContextDescriptorPage,
  normalizeContextPage,
  normalizePiecePage,
} from "./MarkdownPageNormalization.ts";
import {
  copyCursor,
  copyMessageCursor,
  copyOpenResponse,
  isIndex,
} from "./MarkdownResponseValidation.ts";

const imageMediaTypes = ["image/png", "image/jpeg", "image/webp"];

// Source facts are exposed for the later transcript integration; the renderer uses the paged API.
export interface MarkdownOwnedTextGeneration extends MarkdownOwnedGeneration {
  readonly source: TranscriptTextFacts;
  sourceCursor(sourceUtf8Offset: number, signal: AbortSignal): Promise<MessageContentCursor>;
  locateSource(cursor: MessageContentCursor, signal: AbortSignal): Promise<MarkdownRenderCursor>;
}

export interface MarkdownClientRequest extends MarkdownGenerationRequest {
  readonly imageLoader: MarkdownImageLoader | null;
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
      imageLoader: entry.kind === "text" ? this.createImageLoader(sessionId, entry) : null,
    };
  }

  private createImageLoader(sessionId: string, entry: TranscriptEntryRef): MarkdownImageLoader {
    const path = `/api/sessions/${encodeURIComponent(sessionId)}/markdown-images/${encodeURIComponent(entry.executionId)}/${encodeURIComponent(entry.entryId)}`;
    const revision = String(entry.historyRevision);
    return {
      scope: `${this.origin}${path}?historyRevision=${revision}`,
      load: async (key, signal) => {
        if (!isMarkdownImageKey(key)) throw new TypeError("Invalid Markdown image key.");
        const url = new URL(`${path}/${key}`, this.origin);
        url.searchParams.set("historyRevision", revision);
        return requestBlob(url, imageMediaTypes, signal);
      },
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
