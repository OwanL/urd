import type { LlmSelection } from "../../providers/LlmSelection.ts";
import type {
  MessageContentCursor,
  MessageContentPage,
  TranscriptEntryRef,
  TranscriptPosition,
  TranscriptPositionSummary,
  TranscriptPositionWindow,
  TranscriptPromptPage,
} from "../../sessions/transcript/Messages.ts";
import type {
  CompactionReceipt,
  ManualCompactionRequest,
  CompactionSummary,
} from "../../sessions/inputs/Compaction.ts";
import type { SessionSnapshot } from "../../sessions/SessionSnapshot.ts";
import type {
  ContinuationReceipt,
  ContinuationSubmission,
} from "../../sessions/inputs/Continuation.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../../sessions/inputs/UserMessageSubmission.ts";

export const sessionId = "10000000-0000-0000-0000-000000000001";
export const executionId = "20000000-0000-0000-0000-000000000002";
export const submissionId = "30000000-0000-0000-0000-000000000003";
export const operationId = "60000000-0000-0000-0000-000000000006";
export const boundaryId = "80000000-0000-0000-0000-000000000008";
export const compactionId = "70000000-0000-0000-0000-000000000007";
export const cursor: MessageContentCursor = {
  message: {
    executionId,
    messageId: "40000000-0000-0000-0000-000000000004",
    role: "assistant",
    historyRevision: 7,
  },
  segmentIndex: 3,
  utf8Offset: 11,
};
export const submission: UserMessageSubmission = {
  submissionId,
  expectedHistoryRevision: 7,
  text: "  unchanged\r\nUnicode: 雪 🦉\t  ",
  llmSelection: null,
};
export const receipt: UserMessageReceipt = {
  sessionId,
  submission,
  acceptance: "accepted",
  refusalReason: null,
  executionId,
};
export const snapshot: SessionSnapshot = {
  sessionId,
  historyRevision: 7,
  workingDirectory: "C:\\workspace",
  canChangeWorkingDirectory: false,
  admissionFenced: false,
  canContinue: false,
  unsettledExecutionId: executionId,
  stopRequested: false,
  lastSettlement: null,
  latestLlmSelection: null,
  firstUserPreview: "first input preview",
  title: "Backend title",
  pruning: [{ category: "tools", state: "running", total: 4, retained: 3, error: null }],
  pendingInputs: [{ submissionId, preview: "queued follow-up", isClaimed: false }],
};
export const selection: LlmSelection = {
  connection: {
    id: "50000000-0000-0000-0000-000000000005",
    name: "Saved connection 雪",
    route: "chatGpt",
    api: "openAIResponses",
    endpoint: "https://provider.example/v1/",
    authentication: "oAuth",
  },
  modelId: "provider/model-id",
  family: "other",
  supportsImages: false,
  contextWindow: null,
  reasoningEffort: null,
};
export const continuationSubmission: ContinuationSubmission = {
  operationId,
  expectedHistoryRevision: 7,
  expectedLastExecutionId: executionId,
  llmSelection: selection,
};
export const continuationReceipt: ContinuationReceipt = {
  sessionId,
  operationId,
  executionId: operationId,
};
export const content: MessageContentPage = {
  from: cursor,
  text: "streamed text 雪",
  next: { ...cursor, utf8Offset: 28 },
  availableThrough: { ...cursor, utf8Offset: 28 },
  hasMore: false,
  isClosed: false,
  finalOutputCommitted: false,
};
export const promptRef: TranscriptEntryRef = {
  executionId,
  entryId: submissionId,
  kind: "user",
  historyRevision: 7,
};
export const position: TranscriptPosition = {
  ...promptRef,
  rank: 3,
  weight: 288,
  prefixWeight: 900,
};
export const positionSummary: TranscriptPositionSummary = {
  sessionId,
  historyRevision: 7,
  positionVersion: 12,
  entryCount: 8,
  totalWeight: 2300,
};
export const positionWindow: TranscriptPositionWindow = {
  state: positionSummary,
  target: position,
  entries: [
    {
      descriptor: { entry: promptRef, createdAt: null, text: null, tool: null },
      position,
    },
  ],
  toolLabels: {},
  hasBefore: true,
  hasAfter: false,
  settlements: [],
};
export const promptPage: TranscriptPromptPage = {
  state: positionSummary,
  prompts: [position],
  nextAfterRank: 3,
};
export const prepassDetails = {
  executionId,
  boundaryId,
  category: "skills",
  state: "completed",
  modelId: "provider/model-id",
  reasoningEffort: "medium",
  startedAt: "2026-10-12T12:00:00Z",
  finishedAt: "2026-10-12T12:00:02Z",
  costUsd: 0.0003,
  costIsEstimated: true,
  candidateCount: 2,
  resultCount: 0,
  candidates: [{ id: "tool:skill", label: "skill", path: null }],
  results: [],
  candidateOffset: 1,
  resultOffset: 1,
  reasoning: "",
  output: "provider-emitted preview",
  reasoningTruncated: false,
  outputTruncated: false,
  title: null,
  error: null,
};

export const opaqueCursor = "opaque /+?&=#%雪";
export const page = { sessionIds: [sessionId], nextCursor: opaqueCursor };
export const notSentPage = {
  inputs: [{ submissionId, executionId, preview: "not-sent preview" }],
  nextCursor: null,
};
export const stopReceipt = { executionId, status: "requested", outcome: null };
export const compactionSubmission: ManualCompactionRequest = {
  operationId,
  expectedHistoryRevision: 7,
  llmSelection: selection,
  focus: "Preserve decisions and unresolved work.",
};
export const compactionReceipt: CompactionReceipt = { executionId, outcome: null };
export const compactionSummary: CompactionSummary = {
  id: compactionId,
  summary: "The conversation summary.",
  reason: "manual",
  estimatedContextTokensBefore: 8_000,
  estimatedContextTokensAfter: 900,
  createdAt: "2026-10-12T12:00:00Z",
};
