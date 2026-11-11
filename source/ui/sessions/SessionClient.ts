import { requestJson } from "../http/requestJson.ts";
import type {
  MessageContentCursor,
  MessageContentPage,
  NotSentPage,
  TranscriptEntryRef,
  TranscriptHistoryPage,
  TranscriptHistoryQuery,
  TranscriptPositionSummary,
  TranscriptPositionWindow,
  TranscriptPromptPage,
  TranscriptPromptPageQuery,
} from "./Messages.ts";
import type { SessionIdPage } from "./SessionIdPage.ts";
import type { SessionSnapshot } from "./SessionSnapshot.ts";
import type { StopReceipt } from "./Stop.ts";
import type { UserMessageReceipt, UserMessageSubmission } from "./UserMessageSubmission.ts";

// Typed bounded HTTP reads and commands. Page events and receipt reconciliation stay with callers.
export class SessionClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  list(cursor?: string, signal?: AbortSignal): Promise<SessionIdPage> {
    const url = this.url("");
    if (cursor !== undefined) url.searchParams.set("cursor", cursor);
    return requestJson(url, "GET", undefined, signal);
  }

  async create(): Promise<string> {
    const result = await requestJson<{ sessionId: string }>(this.url(""), "POST", {});
    const id = result?.sessionId;
    if (
      typeof id !== "string" ||
      !/^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i.test(id) ||
      id === "00000000-0000-0000-0000-000000000000"
    )
      throw new TypeError("Server returned an invalid session identity.");
    return id;
  }

  getSnapshot(sessionId: string, signal?: AbortSignal): Promise<SessionSnapshot> {
    return requestJson(
      this.url(`/${encodeURIComponent(sessionId)}/snapshot`),
      "GET",
      undefined,
      signal,
    );
  }

  getNotSent(sessionId: string, cursor?: string, signal?: AbortSignal): Promise<NotSentPage> {
    const url = this.url(`/${encodeURIComponent(sessionId)}/not-sent`);
    if (cursor !== undefined) url.searchParams.set("cursor", cursor);
    return requestJson(url, "GET", undefined, signal);
  }

  getHistory(
    sessionId: string,
    query: TranscriptHistoryQuery,
    signal?: AbortSignal,
  ): Promise<TranscriptHistoryPage> {
    const url = this.url(`/${encodeURIComponent(sessionId)}/history`);
    url.searchParams.set("mode", query.mode);
    url.searchParams.set("expectedHistoryRevision", String(query.expectedHistoryRevision));
    if (query.mode !== "latest") {
      const { anchor } = query;
      if (anchor.historyRevision !== query.expectedHistoryRevision)
        throw new TypeError("History anchor must belong to the expected revision.");
      url.searchParams.set("executionId", anchor.executionId);
      url.searchParams.set("entryId", anchor.entryId);
      url.searchParams.set("kind", anchor.kind);
    }
    return requestJson(url, "GET", undefined, signal);
  }

  getPositionSummary(
    sessionId: string,
    expectedHistoryRevision: number,
    signal?: AbortSignal,
  ): Promise<TranscriptPositionSummary> {
    const url = this.url(`/${encodeURIComponent(sessionId)}/positions`);
    url.searchParams.set("expectedHistoryRevision", String(expectedHistoryRevision));
    return requestJson(url, "GET", undefined, signal);
  }

  locatePosition(
    sessionId: string,
    expectedHistoryRevision: number,
    fraction: number,
    signal?: AbortSignal,
  ): Promise<TranscriptPositionWindow> {
    if (!Number.isFinite(fraction) || fraction < 0 || fraction > 1)
      throw new TypeError("Position fraction must be between 0 and 1.");
    const url = this.url(`/${encodeURIComponent(sessionId)}/positions/locate`);
    url.search = new URLSearchParams({
      expectedHistoryRevision: String(expectedHistoryRevision),
      fraction: String(fraction),
    }).toString();
    return requestJson(url, "GET", undefined, signal);
  }

  getEntryPosition(
    sessionId: string,
    entry: TranscriptEntryRef,
    signal?: AbortSignal,
  ): Promise<TranscriptPositionWindow> {
    const url = this.url(
      `/${encodeURIComponent(sessionId)}/positions/entries/${encodeURIComponent(entry.executionId)}/${encodeURIComponent(entry.entryId)}`,
    );
    url.search = new URLSearchParams({
      expectedHistoryRevision: String(entry.historyRevision),
      kind: entry.kind,
    }).toString();
    return requestJson(url, "GET", undefined, signal);
  }

  getPromptMarkers(
    sessionId: string,
    query: TranscriptPromptPageQuery,
    signal?: AbortSignal,
  ): Promise<TranscriptPromptPage> {
    if (
      query.afterRank !== undefined &&
      (!Number.isSafeInteger(query.afterRank) || query.afterRank < 0)
    )
      throw new TypeError("Prompt cursor must be a nonnegative rank.");
    if (
      query.limit !== undefined &&
      (!Number.isInteger(query.limit) || query.limit < 1 || query.limit > 60)
    )
      throw new TypeError("Prompt page limit must be between 1 and 60.");
    const url = this.url(`/${encodeURIComponent(sessionId)}/positions/prompts`);
    url.searchParams.set("expectedHistoryRevision", String(query.expectedHistoryRevision));
    if (query.afterRank !== undefined) url.searchParams.set("afterRank", String(query.afterRank));
    if (query.limit !== undefined) url.searchParams.set("limit", String(query.limit));
    return requestJson(url, "GET", undefined, signal);
  }

  activatePrompt(
    sessionId: string,
    prompt: TranscriptEntryRef,
    signal?: AbortSignal,
  ): Promise<TranscriptPositionWindow> {
    if (
      prompt.kind !== "user" ||
      !Number.isSafeInteger(prompt.historyRevision) ||
      prompt.historyRevision < 0
    )
      throw new TypeError("Prompt activation requires a user entry from a valid history revision.");
    const path =
      `/${encodeURIComponent(sessionId)}/positions/prompts/${encodeURIComponent(prompt.executionId)}` +
      `/${encodeURIComponent(prompt.entryId)}`;
    const url = this.url(path);
    url.searchParams.set("expectedHistoryRevision", String(prompt.historyRevision));
    return requestJson(url, "GET", undefined, signal);
  }

  getMessageContent(
    sessionId: string,
    cursor: MessageContentCursor,
    signal?: AbortSignal,
  ): Promise<MessageContentPage> {
    const url = this.url(`/${encodeURIComponent(sessionId)}/content`);
    const { message, segmentIndex, utf8Offset } = cursor;
    url.search = new URLSearchParams({
      executionId: message.executionId,
      messageId: message.messageId,
      role: message.role,
      historyRevision: String(message.historyRevision),
      segmentIndex: String(segmentIndex),
      utf8Offset: String(utf8Offset),
    }).toString();
    return requestJson(url, "GET", undefined, signal);
  }

  submitMessage(sessionId: string, submission: UserMessageSubmission): Promise<UserMessageReceipt> {
    // A caller must match the full original submission before resolving an unknown Send.
    return requestJson(
      this.url(`/${encodeURIComponent(sessionId)}/message-submissions`),
      "POST",
      submission,
    );
  }

  getMessageReceipt(
    sessionId: string,
    submissionId: string,
    signal?: AbortSignal,
  ): Promise<UserMessageReceipt | null> {
    const path = `/${encodeURIComponent(sessionId)}/message-submissions/${encodeURIComponent(submissionId)}`;
    return requestJson(this.url(path), "GET", undefined, signal);
  }

  stopExecution(sessionId: string, executionId: string): Promise<StopReceipt> {
    const path = `/${encodeURIComponent(sessionId)}/executions/${encodeURIComponent(executionId)}/stop`;
    return requestJson(this.url(path), "POST", {});
  }

  private url(path: string): URL {
    return new URL(`/api/sessions${path}`, this.origin);
  }
}
