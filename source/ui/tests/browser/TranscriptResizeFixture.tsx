import { render } from "preact";
import { VirtualTranscript } from "../../chat/transcript/VirtualTranscript.tsx";
import type { TranscriptDisplay } from "../../chat/transcript/TranscriptDisplay.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type {
  TranscriptEntryDescriptor,
  TranscriptPositionWindow,
} from "../../sessions/transcript/Messages.ts";
import "../../base.css";

const answer = "First answer line\nSecond answer line\nThird answer line\nFourth answer line";
const toolEntry = {
  executionId: "execution",
  entryId: "answer",
  kind: "tool",
  historyRevision: 1,
} as const;
const textEntry = {
  executionId: "execution",
  entryId: "reply",
  kind: "text",
  historyRevision: 1,
} as const;
const cursor = {
  message: { executionId: "execution", messageId: "reply", role: "assistant", historyRevision: 1 },
  segmentIndex: 0,
  utf8Offset: 0,
} as const;
const descriptors: TranscriptEntryDescriptor[] = [
  {
    entry: toolEntry,
    createdAt: null,
    text: null,
    tool: {
      requestId: "request",
      callId: "call",
      name: "answer_agent",
      started: true,
      interrupted: false,
      background: false,
      batchedWithPrevious: false,
      batchedWithNext: false,
      outcome: "completed",
      isClosed: true,
      startedAt: null,
      endedAt: null,
    },
  },
  {
    entry: textEntry,
    createdAt: null,
    tool: null,
    text: {
      from: cursor,
      availableThrough: { ...cursor, utf8Offset: 25 },
      isClosed: true,
      finalOutputCommitted: true,
    },
  },
];
const windowData: TranscriptPositionWindow = {
  state: {
    sessionId: "session",
    historyRevision: 1,
    positionVersion: 1,
    entryCount: 2,
    totalWeight: 256 + 281,
  },
  target: null,
  entries: descriptors.map((descriptor, rank) => ({
    descriptor,
    position: {
      ...descriptor.entry,
      rank,
      weight: rank === 0 ? 256 : 281,
      prefixWeight: rank === 0 ? 0 : 256,
    },
  })),
  toolLabels: {
    "request/call": { icon: "answerAgent", title: "Answer", detail: null, path: null },
  },
  hasBefore: false,
  hasAfter: false,
  settlements: [],
};
const client = {
  locatePosition: async () => windowData,
  getEntryPosition: async () => windowData,
  readToolCallTranscript: async () => ({
    input: JSON.stringify({ answer }),
    output: null,
    truncated: false,
    projectionFinal: true,
    inputTokens: null,
    outputTokens: null,
  }),
} as unknown as SessionClient;
const display = {
  MessageBody: () => <div class="message message-assistant">Following assistant reply</div>,
} as unknown as TranscriptDisplay;

render(
  <div style={{ display: "flex", height: "100%" }}>
    <VirtualTranscript
      client={client}
      display={display}
      streamId={null}
      sessionId="session"
      historyRevision={1}
      notice={null}
      visible
      navigation={undefined}
      onNavigationChange={() => {}}
    />
  </div>,
  document.body,
);
