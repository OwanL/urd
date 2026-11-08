import type { MessageContentCursor, MessageContentPage } from "./Messages.ts";
import type { SessionIdPage } from "./SessionIdPage.ts";
import { SessionRequestError } from "./SessionRequestError.ts";
import type { SessionSnapshot } from "./SessionSnapshot.ts";
import type { StopReceipt } from "./Stop.ts";
import type { UserMessageReceipt, UserMessageSubmission } from "./UserMessageSubmission.ts";

// Typed HTTP access only. Callers own polling, exact-receipt reconciliation and UI state.
export class SessionClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  list(cursor?: string, signal?: AbortSignal): Promise<SessionIdPage> {
    const url = this.url("");
    if (cursor !== undefined) url.searchParams.set("cursor", cursor);
    return this.request(url, "GET", undefined, signal);
  }

  async create(): Promise<string> {
    const result = await this.request<{ sessionId: string }>(this.url(""), "POST", {});
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
    return this.request(
      this.url(`/${encodeURIComponent(sessionId)}/snapshot`),
      "GET",
      undefined,
      signal,
    );
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
    return this.request(url, "GET", undefined, signal);
  }

  submitMessage(sessionId: string, submission: UserMessageSubmission): Promise<UserMessageReceipt> {
    return this.request(
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
    return this.request(this.url(path), "GET", undefined, signal);
  }

  stopExecution(sessionId: string, executionId: string): Promise<StopReceipt> {
    const path = `/${encodeURIComponent(sessionId)}/executions/${encodeURIComponent(executionId)}/stop`;
    return this.request(this.url(path), "POST", {});
  }

  private url(path: string): URL {
    return new URL(`/api/sessions${path}`, this.origin);
  }

  private async request<T>(
    url: URL,
    method: "GET" | "POST",
    body?: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    const headers: Record<string, string> = {
      "X-Urd-Client": "browser",
      Accept: "application/json",
    };
    const options: RequestInit = {
      method,
      headers,
      cache: "no-store",
      credentials: "omit",
      redirect: "error",
    };
    if (method === "POST") {
      headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    if (signal !== undefined) options.signal = signal;

    const response = await fetch(url, options);
    if (!response.ok)
      throw new SessionRequestError(response.status, await this.readErrorBody(response));
    const mediaType = response.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase();
    if (mediaType !== "application/json") {
      await response.body?.cancel();
      throw new TypeError("Server returned a non-JSON session response.");
    }
    // These wire types describe our server, not a general runtime schema validator.
    // A caller must match the full original submission before resolving an unknown Send.
    return (await response.json()) as T;
  }

  private async readErrorBody(response: Response): Promise<unknown> {
    let text: string;
    try {
      text = await response.text();
    } catch {
      // Even a broken error body must not erase an already-received HTTP status.
      return undefined;
    }
    if (!text) return text;
    try {
      return JSON.parse(text) as unknown;
    } catch {
      return text;
    }
  }
}
