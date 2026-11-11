import type { CompactionReason } from "./Compaction.ts";
import type { ExecutionOutcome, SessionExecutionResult } from "./SessionExecutionResult.ts";

// Identifies one message within a session's history revision.
export interface MessageRef {
  readonly executionId: string;
  readonly messageId: string;
  readonly role: MessageRole;
  readonly historyRevision: number;
}

// Entry order is observed order, not request order or a displayed rank.
export type TranscriptEntryKind = "user" | "text" | "tool" | "toolCompletion" | "compaction";

export interface TranscriptEntryRef {
  readonly executionId: string;
  readonly entryId: string;
  readonly kind: TranscriptEntryKind;
  readonly historyRevision: number;
}

export function isTextEntry(entry: TranscriptEntryRef): boolean {
  return entry.kind === "user" || entry.kind === "text";
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
  readonly compaction?: TranscriptCompactionFacts;
}

export interface TranscriptCompactionFacts {
  readonly reason: CompactionReason;
  readonly tokensBefore: number;
  readonly tokensAfter: number;
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

export interface TranscriptPositionSummary {
  readonly sessionId: string;
  readonly historyRevision: number;
  readonly positionVersion: number;
  readonly entryCount: number;
  readonly totalWeight: number;
}

export interface TranscriptPosition {
  readonly executionId: string;
  readonly entryId: string;
  readonly kind: TranscriptEntryKind;
  readonly historyRevision: number;
  readonly rank: number;
  readonly weight: number;
  readonly prefixWeight: number;
}

export interface TranscriptPositionedEntry {
  readonly descriptor: TranscriptEntryDescriptor;
  readonly position: TranscriptPosition;
}

export interface TranscriptPositionWindow {
  readonly state: TranscriptPositionSummary;
  readonly target: TranscriptPosition | null;
  readonly entries: readonly TranscriptPositionedEntry[];
  readonly hasBefore: boolean;
  readonly hasAfter: boolean;
  readonly settlements: readonly SessionExecutionResult[];
}

export interface TranscriptPromptPage {
  readonly state: TranscriptPositionSummary;
  readonly prompts: readonly TranscriptPosition[];
  readonly nextAfterRank: number | null;
}

export interface TranscriptPromptPageQuery {
  readonly expectedHistoryRevision: number;
  readonly afterRank?: number;
  readonly limit?: number;
}

export interface NotSentInput {
  readonly submissionId: string;
  readonly executionId: string;
  readonly preview: string;
}

export interface NotSentPage {
  readonly inputs: readonly NotSentInput[];
  readonly nextCursor: string | null;
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
