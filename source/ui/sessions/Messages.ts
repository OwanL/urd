import type { ExecutionOutcome, SessionExecutionResult } from "./SessionExecutionResult.ts";

// Identifies one message within a session's history revision.
export interface MessageRef {
  readonly executionId: string;
  readonly messageId: string;
  readonly role: MessageRole;
  readonly historyRevision: number;
}

// Entry order is observed order, not request order or a displayed rank.
export type TranscriptEntryKind = "user" | "text" | "tool";

export interface TranscriptEntryRef {
  readonly executionId: string;
  readonly entryId: string;
  readonly kind: TranscriptEntryKind;
  readonly historyRevision: number;
}

export type TranscriptHistoryQuery =
  | { readonly mode: "latest"; readonly expectedHistoryRevision: number }
  | {
      readonly mode: "before" | "after" | "around";
      readonly expectedHistoryRevision: number;
      readonly anchor: TranscriptEntryRef;
    };

export interface TranscriptEntryDescriptor {
  readonly entry: TranscriptEntryRef;
  readonly createdAt: string | null;
  readonly text: TranscriptTextFacts | null;
  readonly tool: TranscriptToolFacts | null;
}

export interface TranscriptTextFacts {
  readonly from: MessageContentCursor;
  readonly availableThrough: MessageContentCursor;
  readonly isClosed: boolean;
  readonly finalOutputCommitted: boolean;
}

export interface TranscriptToolFacts {
  readonly requestId: string;
  readonly callId: string;
  readonly name: string | null;
  readonly started: boolean;
  readonly outcome: ExecutionOutcome | null;
  readonly isClosed: boolean;
  readonly startedAt: string | null;
  readonly endedAt: string | null;
}

export interface TranscriptHistoryPage {
  readonly sessionId: string;
  readonly historyRevision: number;
  readonly entries: readonly TranscriptEntryDescriptor[];
  readonly hasBefore: boolean;
  readonly hasAfter: boolean;
  readonly settlements: readonly SessionExecutionResult[];
}

// Who wrote a displayed message.
export type MessageRole = "user" | "assistant";

// A short message prefix; availableThrough advertises committed content, not completion.
export interface MessagePreview {
  readonly message: MessageRef;
  readonly preview: string;
  readonly truncated: boolean;
  readonly availableThrough: MessageContentCursor;
  readonly finalOutputCommitted: boolean;
  readonly settlement: ExecutionOutcome | null;
}

// A segment-local UTF-8 byte position, not a JavaScript string index.
export interface MessageContentCursor {
  readonly message: MessageRef;
  readonly segmentIndex: number;
  readonly utf8Offset: number;
}

// One bounded content page; closed output is distinct from settled execution.
export interface MessageContentPage {
  readonly from: MessageContentCursor;
  readonly text: string;
  readonly next: MessageContentCursor;
  readonly availableThrough: MessageContentCursor;
  readonly hasMore: boolean;
  readonly isClosed: boolean;
  readonly finalOutputCommitted: boolean;
}
