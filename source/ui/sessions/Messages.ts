import type { ExecutionOutcome } from './SessionExecutionResult.ts';

// Identifies one message within a session's history revision.
export interface MessageRef {
  readonly executionId: string;
  readonly messageId: string;
  readonly role: MessageRole;
  readonly historyRevision: number;
}

// Who wrote a displayed message.
export type MessageRole = 'user' | 'assistant';

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
