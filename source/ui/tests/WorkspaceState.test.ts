import assert from "node:assert/strict";
import test from "node:test";
import {
  applyReceipt,
  beginSubmission,
  decodeWorkspace,
  editDraft,
  emptyWorkspace,
  encodeWorkspace,
  executionSettled,
  newChat,
  submissionTextError,
} from "../app/WorkspaceState.ts";
import type { WorkspaceState } from "../app/WorkspaceState.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";

void test("text validation uses strict Unicode and the server's UTF-8 byte limit without trimming", () => {
  assert.equal(submissionTextError("  \r\n雪 🦉  "), null);
  assert.equal(submissionTextError("🦉".repeat(4096)), null);
  assert.match(submissionTextError("🦉".repeat(4096) + " ") ?? "", /16 KiB/);
  for (const text of ["\ud800", "\udfff", "x\ud800y", "\udfff🦉"])
    assert.match(submissionTextError(text) ?? "", /Unicode/);
});

const sessionId = "10000000-0000-0000-0000-000000000001";
const executionId = "20000000-0000-0000-0000-000000000002";
const selection: LlmSelection = {
  connection: {
    id: "50000000-0000-0000-0000-000000000005",
    name: "Saved connection 雪",
    route: "chatGpt",
    api: "openAIResponses",
    endpoint: "https://provider.example/v1/",
    authentication: "oAuth",
  },
  modelId: "provider/model-id",
  reasoningEffort: null,
};
void test("causal snapshot reads release accepted work outside the recent window without trusting an earlier read", () => {
  const newer = "20000000-0000-0000-0000-000000000003";
  const snapshot: SessionSnapshot = {
    sessionId,
    historyRevision: 7,
    admissionFenced: false,
    unsettledExecutionId: null,
    stopRequested: false,
    lastSettlement: { executionId: newer, outcome: "completed" },
    latestLlmSelection: selection,
    messages: [],
    hasOlderMessages: true,
  };
  assert.equal(executionSettled(snapshot, executionId, null), false);
  assert.equal(executionSettled(snapshot, executionId, newer), false);
  assert.equal(executionSettled(snapshot, executionId, executionId), true);
  assert.equal(
    executionSettled({ ...snapshot, unsettledExecutionId: executionId }, executionId, executionId),
    false,
  );
  assert.equal(
    executionSettled({ ...snapshot, unsettledExecutionId: newer }, executionId, executionId),
    true,
  );
  assert.equal(
    executionSettled(
      { ...snapshot, lastSettlement: { executionId, outcome: "cancelled" } },
      executionId,
      null,
    ),
    true,
  );
});

const original: UserMessageSubmission = {
  submissionId: "30000000-0000-0000-0000-000000000003",
  expectedHistoryRevision: 7,
  text: "  unchanged\r\nUnicode: 雪 🦉\t  ",
  llmSelection: selection,
};
const receipt: UserMessageReceipt = {
  sessionId,
  submission: original,
  acceptance: "accepted",
  refusalReason: null,
  executionId,
};

function sending(submission = original) {
  return beginSubmission(editDraft(newChat(submission.llmSelection), submission.text), submission);
}

function workspace(): WorkspaceState {
  return {
    version: 1,
    tabs: [sessionId],
    activeId: sessionId,
    chats: { [sessionId]: sending() },
    lastSelection: selection,
  };
}

void test("new state is independent, empty and does not invent a selection", () => {
  assert.deepEqual(emptyWorkspace(), {
    version: 1,
    tabs: [],
    activeId: null,
    chats: {},
    lastSelection: null,
  });
  assert.notEqual(emptyWorkspace().chats, emptyWorkspace().chats);
  const chat = newChat(null);
  assert.equal(chat.selection, null);
  assert.equal(chat.draft, "");
  assert.equal(chat.draftVersion, 0);
  assert.equal(chat.pending, null);
  assert.equal(chat.knownExecutionId, null);
  assert.equal(chat.error, null);
  assert.equal(typeof chat.title, "string");
  assert.deepEqual(decodeWorkspace(encodeWorkspace(emptyWorkspace())), emptyWorkspace());
});

void test("draft edits increment the version without mutating earlier state", () => {
  const chat = newChat(selection);
  const edited = editDraft(chat, original.text);
  const repeated = editDraft(edited, original.text);
  assert.equal(chat.draft, "");
  assert.equal(chat.draftVersion, 0);
  assert.equal(edited.draft, original.text);
  assert.equal(edited.draftVersion, 1);
  assert.equal(repeated.draftVersion, 2);
  assert.equal(edited.selection, selection);
});

void test("beginSubmission saves the full original and version and clears an old error", () => {
  const chat = { ...editDraft(newChat(null), original.text), error: "Previous refusal" };
  const pending = beginSubmission(chat, original);
  assert.deepEqual(pending.pending, { original, draftVersion: 1, status: "sending" });
  assert.equal(pending.error, null);
  assert.equal(pending.draft, original.text);
  assert.equal(chat.pending, null);
  assert.equal(chat.error, "Previous refusal");
  assert.notEqual(pending.pending?.original, original);
  assert.notEqual(pending.pending?.original.llmSelection?.connection, selection.connection);
});

void test("an exact accepted receipt clears only the submitted draft and records execution identity", () => {
  const chat = sending();
  const updated = applyReceipt(chat, sessionId, JSON.parse(JSON.stringify(receipt)));
  assert.equal(updated.draft, "");
  assert.equal(updated.draftVersion, chat.draftVersion);
  assert.equal(updated.pending, null);
  assert.equal(updated.knownExecutionId, executionId);
  assert.equal(updated.error, null);
  assert.equal(chat.draft, original.text);
  assert.notEqual(chat.pending, null);
});

void test("a null initiating selection is preserved and matched, not replaced by the current selection", () => {
  const submission = { ...original, llmSelection: null };
  const chat = { ...sending(submission), selection };
  const updated = applyReceipt(chat, sessionId, { ...receipt, submission });
  assert.equal(updated.draft, "");
  assert.equal(updated.selection, selection);
  assert.throws(() => applyReceipt(chat, sessionId, receipt), /original submission/);
  assert.throws(
    () => applyReceipt(sending(), sessionId, { ...receipt, submission }),
    /original submission/,
  );
});

void test("a later edit is preserved even when its text is changed back to the original", () => {
  const chat = sending();
  for (const edited of [
    editDraft(chat, "New draft"),
    editDraft(editDraft(chat, "x"), original.text),
  ]) {
    const updated = applyReceipt(edited, sessionId, receipt);
    assert.equal(updated.draft, edited.draft);
    assert.equal(updated.draftVersion, edited.draftVersion);
    assert.equal(updated.pending, null);
    assert.equal(updated.knownExecutionId, executionId);
  }
  const unrelated = beginSubmission(editDraft(newChat(null), "Unrelated draft"), original);
  assert.equal(applyReceipt(unrelated, sessionId, receipt).draft, "Unrelated draft");
});

void test("every refusal preserves the draft and reports its actual reason", () => {
  const errors = new Set<string>();
  for (const refusalReason of [
    "staleRevision",
    "capacity",
    "stopping",
    "selectionLocked",
  ] as const) {
    const chat = { ...sending(), knownExecutionId: executionId };
    const updated = applyReceipt(chat, sessionId, {
      ...receipt,
      acceptance: "refused",
      refusalReason,
      executionId: null,
    });
    assert.equal(updated.draft, original.text);
    assert.equal(updated.draftVersion, chat.draftVersion);
    assert.equal(updated.pending, null);
    assert.equal(updated.knownExecutionId, null);
    assert.ok(updated.error);
    errors.add(updated.error);
  }
  assert.equal(errors.size, 4);
});

void test("foreign sessions, absent pending submissions and full original mismatches throw", () => {
  const chat = sending();
  assert.throws(() => applyReceipt(chat, "another-session", receipt), /another session/);
  assert.throws(() => applyReceipt(newChat(null), sessionId, receipt), /original submission/);
  const changes: Partial<UserMessageSubmission>[] = [
    { submissionId: "another-submission" },
    { expectedHistoryRevision: 8 },
    { text: original.text.trim() },
    { text: original.text.replace("\r\n", "\n") },
    { llmSelection: null },
    { llmSelection: { ...selection, modelId: "another-model" } },
    { llmSelection: { ...selection, reasoningEffort: "high" } },
    ...Object.entries({
      id: "another-connection",
      name: "Renamed connection",
      route: "custom",
      api: "openAIChatCompletions",
      endpoint: "https://other.example/v1/",
      authentication: "apiKey",
    }).map(([key, value]) => ({
      llmSelection: { ...selection, connection: { ...selection.connection, [key]: value } },
    })),
  ];
  for (const change of changes)
    assert.throws(
      () => applyReceipt(chat, sessionId, { ...receipt, submission: { ...original, ...change } }),
      /original submission/,
    );
  assert.equal(chat.draft, original.text);
  assert.equal(chat.pending?.status, "sending");
});

void test("original matching is independent of JSON property order", () => {
  const submission = {
    llmSelection: {
      reasoningEffort: selection.reasoningEffort,
      modelId: selection.modelId,
      connection: Object.fromEntries(Object.entries(selection.connection).reverse()),
    },
    text: original.text,
    expectedHistoryRevision: original.expectedHistoryRevision,
    submissionId: original.submissionId,
  };
  assert.equal(applyReceipt(sending(), sessionId, { ...receipt, submission }).draft, "");
});

void test("malformed acceptance decisions never resolve a pending Send", () => {
  const invalid: unknown[] = [
    null,
    [],
    {},
    { ...receipt, sessionId: null },
    { ...receipt, acceptance: "completed" },
    { ...receipt, acceptance: true },
    { ...receipt, executionId: null },
    { ...receipt, executionId: "  " },
    { ...receipt, executionId: 1 },
    { ...receipt, executionId: undefined },
    { ...receipt, refusalReason: "capacity" },
    { ...receipt, refusalReason: undefined },
    { ...receipt, acceptance: "refused", executionId: null },
    { ...receipt, acceptance: "refused", refusalReason: "capacity" },
    { ...receipt, acceptance: "refused", refusalReason: "unknown", executionId: null },
    { ...receipt, acceptance: "refused", refusalReason: "toString", executionId: null },
    { ...receipt, submission: { ...original, llmSelection: undefined } },
    { ...receipt, submission: { ...original, expectedHistoryRevision: -1 } },
    {
      ...receipt,
      submission: { ...original, llmSelection: { ...selection, connection: { id: "x" } } },
    },
  ];
  const chat = sending();
  for (const value of invalid) assert.throws(() => applyReceipt(chat, sessionId, value), TypeError);
  assert.equal(chat.pending?.status, "sending");
});

void test("reload keeps the complete original but marks every saved Send uncertain", () => {
  const state = workspace();
  state.tabs.push("second");
  state.chats.second = sending({ ...original, llmSelection: null });
  const raw = encodeWorkspace(state);
  const reloaded = decodeWorkspace(raw);
  for (const id of state.tabs) {
    const before = state.chats[id]!;
    const after = reloaded.chats[id]!;
    assert.equal(after.pending?.status, "uncertain");
    assert.deepEqual(after.pending?.original, before.pending?.original);
    assert.equal(after.pending?.draftVersion, before.pending?.draftVersion);
    assert.equal(after.draft, before.draft);
  }
  assert.equal(state.chats[sessionId]?.pending?.status, "sending");
  assert.deepEqual(decodeWorkspace(encodeWorkspace(reloaded)), reloaded);
  assert.equal(applyReceipt(reloaded.chats[sessionId]!, sessionId, receipt).draft, "");
});

void test("reload retains later edits while reconciling an uncertain submission", () => {
  const state = workspace();
  state.chats[sessionId] = editDraft(state.chats[sessionId]!, "Saved later draft");
  const restored = decodeWorkspace(encodeWorkspace(state)).chats[sessionId]!;
  const resolved = applyReceipt(restored, sessionId, receipt);
  assert.equal(resolved.draft, "Saved later draft");
  assert.equal(resolved.pending, null);
});

void test("corrupt saved structure throws instead of resetting or discarding drafts", () => {
  for (const raw of ["", "{", "null", "[]", "{}", '"workspace"'])
    assert.throws(() => decodeWorkspace(raw));

  const invalid: unknown[] = [
    { ...workspace(), version: 2 },
    { ...workspace(), tabs: [sessionId, sessionId] },
    { ...workspace(), tabs: ["missing"] },
    { ...workspace(), tabs: [1] },
    { ...workspace(), activeId: "missing" },
    { ...workspace(), chats: [] },
    { ...workspace(), lastSelection: undefined },
    { ...workspace(), lastSelection: { ...selection, connection: { id: "x" } } },
  ];
  const badChatMembers: Record<string, unknown>[] = [
    { draft: null },
    { draftVersion: -1 },
    { draftVersion: 1.5 },
    { draftVersion: Number.MAX_SAFE_INTEGER + 1 },
    { selection: undefined },
    { selection: { ...selection, connection: { ...selection.connection, route: "unknown" } } },
    { selection: { ...selection, reasoningEffort: 3 } },
    { knownExecutionId: false },
    { title: null },
    { error: {} },
    { pending: {} },
    { pending: { original, draftVersion: 1, status: "accepted" } },
    { pending: { original, draftVersion: 2, status: "sending" } },
    { pending: { original, draftVersion: -1, status: "sending" } },
    {
      pending: {
        original: { ...original, llmSelection: undefined },
        draftVersion: 1,
        status: "sending",
      },
    },
    { pending: { original: { ...original, text: null }, draftVersion: 1, status: "sending" } },
  ];
  for (const patch of badChatMembers)
    invalid.push({ ...workspace(), chats: { [sessionId]: { ...sending(), ...patch } } });
  for (const value of invalid)
    assert.throws(() => decodeWorkspace(JSON.stringify(value)), TypeError);
});

void test("encode rejects invalid state rather than silently serializing corrupt numeric data", () => {
  const state = workspace();
  state.chats[sessionId]!.draftVersion = Number.NaN;
  assert.throws(() => encodeWorkspace(state), TypeError);
});

void test("saved chat keys are own properties, including names inherited by ordinary objects", () => {
  const state = emptyWorkspace();
  state.tabs = ["toString"];
  assert.throws(() => decodeWorkspace(JSON.stringify(state)), TypeError);
  state.chats = Object.fromEntries([["__proto__", newChat(null)]]);
  state.tabs = ["__proto__"];
  state.activeId = "__proto__";
  const restored = decodeWorkspace(encodeWorkspace(state));
  assert.equal(Object.hasOwn(restored.chats, "__proto__"), true);
  assert.equal(Object.getPrototypeOf(restored.chats), Object.prototype);
});
