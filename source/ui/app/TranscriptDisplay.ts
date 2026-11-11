import type { ComponentType } from "preact";
import type { TranscriptEntryRef, TranscriptTextFacts } from "../sessions/Messages.ts";
import type { MarkdownClient } from "../markdown-rendering/MarkdownClient.ts";
import type { MarkdownSavedSourcePosition } from "../markdown-rendering/MarkdownMessageView.tsx";

// Supplied only by an explicit composition entry point; normal startup has none.
// These are qualification inputs, not selected production budgets.
export interface TranscriptDisplay {
  readonly MessageBody: ComponentType<RichMessageBodyProps>;
  readonly client: MarkdownClient;
  readonly retainedPieces: number;
  readonly retainedTabs: number;
  readonly release: (work: Promise<void>) => void;
}

export interface RichMessageBodyProps {
  display: TranscriptDisplay;
  streamId: string | null;
  sessionId: string;
  entry: TranscriptEntryRef;
  text: TranscriptTextFacts;
  root: HTMLElement | null;
  paused: boolean;
  saved?: MarkdownSavedSourcePosition;
  registerSource?: (
    capture: ((signal: AbortSignal) => Promise<MarkdownSavedSourcePosition | null>) | null,
  ) => void;
  registerRestore?: (restore: ((saved: MarkdownSavedSourcePosition) => boolean) | null) => void;
  onPublished?: () => void;
}
