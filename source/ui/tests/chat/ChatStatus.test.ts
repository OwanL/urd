import assert from "node:assert/strict";
import test from "node:test";
import { deriveChatStatus } from "../../chat/ChatStatus.ts";
import { composerSubmissionState } from "../../chat/composer/ComposerSubmission.ts";
import type { ChatStopState } from "../../chat/ChatCommands.ts";
import { newChat } from "../../chat/ChatState.ts";

import type { ChatState } from "../../chat/ChatState.ts";
import type { LlmSelection } from "../../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../../sessions/SessionSnapshot.ts";
import type { UserMessageSubmission } from "../../sessions/inputs/UserMessageSubmission.ts";
import type { ContinuationSubmission } from "../../sessions/inputs/Continuation.ts";

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
    workingDirectory: null,
    canChangeWorkingDirectory: true,
    admissionFenced: false,
    canContinue: false,
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

void test("an empty composer submits Continue only when explicitly eligible", () => {
  assert.deepEqual(composerSubmissionState("", false, true, true), {
    action: "continue",
    label: "Continue",
  });
  assert.deepEqual(composerSubmissionState("  ", false, false, false), {
    action: null,
    label: "Send",
  });
  assert.deepEqual(composerSubmissionState("draft", true, true, true), {
    action: "send",
    label: "Send",
  });
  assert.deepEqual(composerSubmissionState("draft", false, true, true), {
    action: null,
    label: "Send",
  });
});

void test("send and resend share execution gates but retain distinct input requirements", () => {
  const available = deriveChatStatus(chat, snapshot(), undefined, true, true);
  assert.equal(available.canSend, true);
  assert.equal(available.canResendNotSent, true);

  const notReady = deriveChatStatus(chat, snapshot(), undefined, false, true);
  assert.equal(notReady.canSend, false);
  assert.equal(notReady.canResendNotSent, true);

  const noSelection = deriveChatStatus(
    { ...chat, selection: null },
    snapshot({ latestLlmSelection: null }),
    undefined,
    true,
    true,
  );
  assert.equal(noSelection.canSend, false);
  assert.equal(noSelection.canResendNotSent, true);

  const noDraft = deriveChatStatus({ ...chat, draft: "  " }, snapshot(), undefined, true, true);
  assert.equal(noDraft.canSend, false);
  assert.equal(noDraft.canResendNotSent, true);

  const invalidDraft = deriveChatStatus(
    { ...chat, draft: "\uD800" },
    snapshot(),
    undefined,
    true,
    true,
  );
  assert.equal(invalidDraft.canSend, false);
  assert.equal(invalidDraft.canResendNotSent, true);

  const cannotPersist = deriveChatStatus(chat, snapshot(), undefined, true, false);
  assert.equal(cannotPersist.canSend, false);
  assert.equal(cannotPersist.canResendNotSent, false);

  const noSnapshot = deriveChatStatus(chat, null, undefined, true, true);
  assert.equal(noSnapshot.canSend, false);
  assert.equal(noSnapshot.canResendNotSent, false);
});

void test("only an unselected idle chat inherits the workspace model", () => {
  const fallback = { ...selection, modelId: "remembered-model" };
  const empty = { ...chat, selection: null };
  const unmatched = snapshot({ latestLlmSelection: null });
  const status = deriveChatStatus(empty, unmatched, undefined, true, true, false, fallback);
  assert.equal(status.selection, fallback);
  assert.equal(status.canSend, true);
  assert.equal(
    deriveChatStatus(chat, unmatched, undefined, true, true, false, fallback).selection,
    selection,
  );
  assert.equal(
    deriveChatStatus(empty, snapshot(), undefined, true, true, false, fallback).selection,
    latestSelection,
  );
  assert.equal(
    deriveChatStatus(
      empty,
      { ...unmatched, unsettledExecutionId: executionId },
      undefined,
      true,
      true,
      false,
      fallback,
    ).selection,
    null,
  );
});

void test("a locked picker shows the journaled selection even if a snapshot shows another model", () => {
  const changed = snapshot({ unsettledExecutionId: otherExecutionId });
  const editing = { ...pendingChat("uncertain"), selection: null };
  const send = deriveChatStatus(editing, changed, undefined, true, true);
  assert.equal(send.selectionLocked, true);
  assert.equal(send.selection, selection);

  const continuation: ContinuationSubmission = {
    operationId: "60000000-0000-0000-0000-000000000006",
    expectedHistoryRevision: changed.historyRevision,
    expectedLastExecutionId: executionId,
    llmSelection: selection,
  };
  const started = {
    ...newChat(null),
    pendingContinuation: { original: continuation, status: "uncertain" as const },
  };
  const continued = deriveChatStatus(started, changed, undefined, true, true);
  assert.equal(continued.selectionLocked, true);
  assert.equal(continued.selection, selection);
});

void test("Continue eligibility requires the core capability, idle session and no local command", () => {
  const settled = snapshot({
    canContinue: true,
    lastSettlement: { executionId, outcome: "completed" },
  });
  const empty = { ...chat, draft: "" };
  const eligible = deriveChatStatus(empty, settled, undefined, true, true);
  assert.equal(eligible.canSend, false);
  assert.equal(eligible.continuationAvailable, true);
  assert.equal(eligible.canContinue, true);

  assert.equal(deriveChatStatus(empty, snapshot(), undefined, true, true).canContinue, false);
  assert.equal(
    deriveChatStatus(
      empty,
      { ...settled, unsettledExecutionId: executionId },
      undefined,
      true,
      true,
    ).canContinue,
    false,
  );
  assert.equal(deriveChatStatus(empty, settled, undefined, false, true).canContinue, false);
  assert.equal(deriveChatStatus(empty, settled, undefined, true, false).canContinue, false);
  assert.equal(deriveChatStatus(empty, settled, undefined, true, true, true).canContinue, false);

  const original: ContinuationSubmission = {
    operationId: "60000000-0000-0000-0000-000000000006",
    expectedHistoryRevision: 7,
    expectedLastExecutionId: executionId,
    llmSelection: selection,
  };
  const uncertain = deriveChatStatus(
    { ...chat, pendingContinuation: { original, status: "uncertain" } },
    settled,
    undefined,
    true,
    true,
  );
  assert.equal(uncertain.selectionLocked, true);
  assert.equal(uncertain.canSend, false);
  assert.equal(uncertain.canContinue, false);
});

void test("active execution steers selection; a blocked run locks without steering it", () => {
  const active = deriveChatStatus(
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

  const fenced = deriveChatStatus(
    chat,
    snapshot({ unsettledExecutionId: executionId, admissionFenced: true }),
    undefined,
    true,
    true,
  );
  assert.equal(fenced.active, false);
  assert.equal(fenced.executionStatus, "blocked-run");
  assert.equal(fenced.selectionLocked, true);
  assert.equal(fenced.selection, selection);
  assert.equal(fenced.canSend, false);
  assert.equal(fenced.canResendNotSent, false);
});

void test("uncertain submissions and known executions retain their distinct status and send gates", () => {
  const uncertain = deriveChatStatus(pendingChat("uncertain"), snapshot(), undefined, true, true);
  assert.equal(uncertain.pendingStatus, "uncertain");
  assert.equal(uncertain.selectionLocked, true);
  assert.equal(uncertain.canSend, false);
  assert.equal(uncertain.canResendNotSent, false);

  const known = deriveChatStatus(
    { ...chat, knownExecutionId: executionId },
    snapshot({ lastSettlement: { executionId: otherExecutionId, outcome: "completed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(known.executionStatus, "accepted");
  assert.equal(known.canSend, false);
  assert.equal(known.canResendNotSent, false);

  const settled = deriveChatStatus(
    { ...chat, knownExecutionId: executionId },
    snapshot({ lastSettlement: { executionId, outcome: "completed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(settled.executionStatus, null);
  assert.equal(settled.canSend, true);
  assert.equal(settled.canResendNotSent, true);
});

void test("compaction blocks steering while ordinary active executions remain steerable", () => {
  const knownChat = { ...chat, knownExecutionId: executionId };
  const running = deriveChatStatus(
    knownChat,
    snapshot({ unsettledExecutionId: executionId }),
    undefined,
    true,
    true,
  );
  assert.equal(running.executionStatus, "running");
  assert.equal(running.canSend, true);
  assert.equal(running.canResendNotSent, true);

  const compacting = deriveChatStatus(
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

void test("a chat that needs recovery shows only the recovery state", () => {
  const blocked = snapshot({
    admissionFenced: true,
    requiresRecovery: true,
    unsettledExecutionId: executionId,
    stopRequested: true,
    lastSettlement: { executionId: otherExecutionId, outcome: "failed" },
  });
  const stop: ChatStopState = { executionId, pending: true, error: null };
  const status = deriveChatStatus(
    { ...chat, knownExecutionId: executionId },
    blocked,
    stop,
    true,
    true,
  );
  assert.equal(status.executionStatus, "recovery-required");
  assert.equal(status.active, false);
  assert.equal(status.canSend, false);
  assert.equal(status.canContinue, false);
  assert.equal(status.canResendNotSent, false);
  assert.equal(
    deriveChatStatus(chat, { ...blocked, canCloseAfterFailure: true }, stop, true, true)
      .executionStatus,
    "recovery-can-close",
  );
});

void test("a blocked chat does not infer a recovery failure", () => {
  const blocked = snapshot({ admissionFenced: true });
  assert.equal(deriveChatStatus(chat, blocked, undefined, true, true).executionStatus, "blocked");
  assert.equal(
    deriveChatStatus(
      { ...chat, knownExecutionId: executionId },
      { ...blocked, unsettledExecutionId: executionId, stopRequested: true },
      undefined,
      true,
      true,
    ).executionStatus,
    "blocked-run",
  );
});

void test("stopping takes priority over settlement facts", () => {
  const stop: ChatStopState = { executionId, pending: true, error: null };
  const stopping = deriveChatStatus(
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
  assert.equal(stopping.canResendNotSent, false);

  const stopRequested = deriveChatStatus(
    chat,
    snapshot({ stopRequested: true, lastSettlement: { executionId, outcome: "failed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(stopRequested.executionStatus, "stopping");
  assert.equal(stopRequested.canResendNotSent, false);

  const failed = deriveChatStatus(
    chat,
    snapshot({ lastSettlement: { executionId, outcome: "failed" } }),
    undefined,
    true,
    true,
  );
  assert.equal(failed.executionStatus, "failed");

  const cancelled = deriveChatStatus(
    chat,
    snapshot({ lastSettlement: { executionId, outcome: "cancelled" } }),
    undefined,
    true,
    true,
  );
  assert.equal(cancelled.executionStatus, "cancelled");
});
