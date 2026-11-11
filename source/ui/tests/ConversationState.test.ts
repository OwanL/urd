import assert from "node:assert/strict";
import test from "node:test";
import { deriveConversationState } from "../app/ConversationState.ts";
import type { WorkspaceStopState } from "../app/WorkspaceCommands.ts";
import { newChat } from "../app/WorkspaceState.ts";
import type { ChatState } from "../app/WorkspaceState.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { UserMessageSubmission } from "../sessions/UserMessageSubmission.ts";

const sessionId = "10000000-0000-0000-0000-000000000001";
const executionId = "20000000-0000-0000-0000-000000000002";
const otherExecutionId = "20000000-0000-0000-0000-000000000003";
const selection: LlmSelection = {
  connection: {
    id: "50000000-0000-0000-0000-000000000005",
    name: "Saved provider",
    route: "custom",
    api: "openAIResponses",
    endpoint: "http://127.0.0.1:1234/v1/",
    authentication: "none",
  },
  modelId: "saved-model",
  family: "other",
  supportsImages: false,
  contextWindow: null,
  reasoningEffort: null,
};
const latestSelection: LlmSelection = { ...selection, modelId: "latest-model" };
const chat: ChatState = { ...newChat(selection), draft: "Send this message" };

function snapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return {
    sessionId,
    historyRevision: 7,
    admissionFenced: false,
    unsettledExecutionId: null,
    stopRequested: false,
    lastSettlement: null,
    latestLlmSelection: latestSelection,
    firstUserPreview: null,
    pendingInputs: [],
    ...overrides,
  };
}

function pendingChat(status: "sending" | "uncertain"): ChatState {
  const original: UserMessageSubmission = {
    submissionId: "30000000-0000-0000-0000-000000000003",
    expectedHistoryRevision: 7,
    text: chat.draft,
    llmSelection: selection,
  };
  return {
    ...chat,
    pending: { original, draftVersion: chat.draftVersion, status },
  };
}

void test("send and resend share execution gates but retain distinct input requirements", () => {
  const available = deriveConversationState(chat, snapshot(), undefined, true, true);
  assert.equal(available.canSend, true);
  assert.equal(available.canResendNotSent, true);

  const notReady = deriveConversationState(chat, snapshot(), undefined, false, true);
  assert.equal(notReady.canSend, false);
  assert.equal(notReady.canResendNotSent, true);

  const noSelection = deriveConversationState(
    { ...chat, selection: null },
    snapshot({ latestLlmSelection: null }),
    undefined,
    true,
    true,
  );
  assert.equal(noSelection.canSend, false);
  assert.equal(noSelection.canResendNotSent, true);

  const noDraft = deriveConversationState(
    { ...chat, draft: "  " },
    snapshot(),
    undefined,
    true,
    true,
  );
  assert.equal(noDraft.canSend, false);
  assert.equal(noDraft.canResendNotSent, true);

  const invalidDraft = deriveConversationState(
    { ...chat, draft: "\uD800" },
    snapshot(),
    undefined,
    true,
    true,
  );
  assert.equal(invalidDraft.canSend, false);
  assert.equal(invalidDraft.canResendNotSent, true);

  const cannotPersist = deriveConversationState(chat, snapshot(), undefined, true, false);
  assert.equal(cannotPersist.canSend, false);
  assert.equal(cannotPersist.canResendNotSent, false);

  const noSnapshot = deriveConversationState(chat, null, undefined, true, true);
  assert.equal(noSnapshot.canSend, false);
  assert.equal(noSnapshot.canResendNotSent, false);
});

void test("active execution steers selection; fencing locks without steering it", () => {
  const active = deriveConversationState(
    { ...chat, knownExecutionId: executionId },
    snapshot({ unsettledExecutionId: executionId }),
    undefined,
    true,
    true,
  );
  assert.equal(active.active, true);
  assert.equal(active.selectionLocked, true);
  assert.equal(active.selection, latestSelection);
  assert.equal(active.executionStatus, "running");

  const fenced = deriveConversationState(
    chat,
    snapshot({ unsettledExecutionId: executionId, admissionFenced: true }),
    undefined,
    true,
    true,
  );
  assert.equal(fenced.active, false);
  assert.equal(fenced.admissionFenced, true);
  assert.equal(fenced.unsettledWhileFenced, true);
  assert.equal(fenced.selectionLocked, true);
  assert.equal(fenced.selection, selection);
  assert.equal(fenced.canSend, false);
  assert.equal(fenced.canResendNotSent, false);
});

void test("uncertain submissions and known executions retain their distinct status and send gates", () => {
  const uncertain = deriveConversationState(
    pendingChat("uncertain"),
    snapshot(),
    undefined,
    true,
    true,
  );
  assert.equal(uncertain.pendingStatus, "uncertain");
  assert.equal(uncertain.selectionLocked, true);
  assert.equal(uncertain.canSend, false);
  assert.equal(uncertain.canResendNotSent, false);

  const known = deriveConversationState(
    { ...chat, knownExecutionId: executionId },
    snapshot({ lastSettlement: { executionId: otherExecutionId, outcome: "completed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(known.executionStatus, "accepted");
  assert.equal(known.canSend, false);
  assert.equal(known.canResendNotSent, false);

  const settled = deriveConversationState(
    { ...chat, knownExecutionId: executionId },
    snapshot({ lastSettlement: { executionId, outcome: "completed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(settled.executionStatus, "accepted");
  assert.equal(settled.canSend, true);
  assert.equal(settled.canResendNotSent, true);
});

void test("compaction blocks steering while ordinary active executions remain steerable", () => {
  const knownChat = { ...chat, knownExecutionId: executionId };
  const running = deriveConversationState(
    knownChat,
    snapshot({ unsettledExecutionId: executionId }),
    undefined,
    true,
    true,
  );
  assert.equal(running.executionStatus, "running");
  assert.equal(running.canSend, true);
  assert.equal(running.canResendNotSent, true);

  const compacting = deriveConversationState(
    knownChat,
    snapshot({ unsettledExecutionId: executionId, isCompacting: true }),
    undefined,
    true,
    true,
  );
  assert.equal(compacting.executionStatus, "compacting");
  assert.equal(compacting.canSend, false);
  assert.equal(compacting.canResendNotSent, false);
});

void test("stopping and explicit stop state take priority over settlement facts", () => {
  const stop: WorkspaceStopState = { executionId, pending: true, error: null };
  const stopping = deriveConversationState(
    chat,
    snapshot({
      unsettledExecutionId: executionId,
      lastSettlement: { executionId: otherExecutionId, outcome: "failed" },
    }),
    stop,
    true,
    true,
  );
  assert.equal(stopping.stopId, executionId);
  assert.equal(stopping.executionStatus, "stopping");
  assert.equal(stopping.settlementStatus, null);
  assert.equal(stopping.canResendNotSent, false);

  const stopRequested = deriveConversationState(
    chat,
    snapshot({ stopRequested: true, lastSettlement: { executionId, outcome: "failed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(stopRequested.showStopRequested, true);
  assert.equal(stopRequested.settlementStatus, null);
  assert.equal(stopRequested.canResendNotSent, false);

  const failed = deriveConversationState(
    chat,
    snapshot({ lastSettlement: { executionId, outcome: "failed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(failed.showStopRequested, false);
  assert.equal(failed.settlementStatus, "failed");

  const cancelled = deriveConversationState(
    chat,
    snapshot({ lastSettlement: { executionId, outcome: "cancelled" } }),
    undefined,
    true,
    true,
  );
  assert.equal(cancelled.settlementStatus, "cancelled");
});
