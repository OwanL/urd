import { requestJson } from "../http/requestJson.ts";
import type {
  MessageContentCursor,
  MessageContentPage,
  TranscriptHistoryPage,
  TranscriptHistoryQuery,
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
