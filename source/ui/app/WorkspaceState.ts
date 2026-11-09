import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  MessageRefusalReason,
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";

export interface ChatState {
  draft: string;
  draftVersion: number;
  selection: LlmSelection | null;
  pending: PendingSubmission | null;
  knownExecutionId: string | null;
  title: string;
  error: string | null;
}

export interface PendingSubmission {
  original: UserMessageSubmission;
  draftVersion: number;
  status: "sending" | "uncertain";
}

export interface WorkspaceState {
  version: 1;
  tabs: string[];
  activeId: string | null;
  chats: Record<string, ChatState>;
  lastSelection: LlmSelection | null;
}

const refusalErrors: Record<MessageRefusalReason, string> = {
  staleRevision: "The conversation changed. Review it before sending again.",
  capacity: "The session cannot accept another message yet.",
  stopping: "The execution is stopping. Wait before sending again.",
  selectionLocked: "The model selection is locked for this execution.",
};

// Reject malformed UTF-16 without trimming or replacing input.
export function submissionTextError(text: string): string | null {
  if (/[\uD800-\uDFFF]/u.test(text)) return "Message contains an incomplete Unicode character.";
  return null;
}

// A read begun after this receipt can prove settlement even after another client
// pushes the execution out of the recent window. Earlier idle reads cannot.
export function executionSettled(
  snapshot: SessionSnapshot,
  executionId: string,
  knownWhenRead: string | null,
): boolean {
  return (
    snapshot.lastSettlement?.executionId === executionId ||
    snapshot.messages.some(
      (message) => message.message.executionId === executionId && message.settlement !== null,
    ) ||
    (knownWhenRead === executionId && snapshot.unsettledExecutionId !== executionId)
  );
}

export function emptyWorkspace(): WorkspaceState {
  return { version: 1, tabs: [], activeId: null, chats: {}, lastSelection: null };
}

export function newChat(selection: LlmSelection | null): ChatState {
  return {
    draft: "",
    draftVersion: 0,
    selection,
    pending: null,
    knownExecutionId: null,
    title: "New chat",
    error: null,
  };
}

export function editDraft(chat: ChatState, text: string): ChatState {
  if (!isRevision(chat.draftVersion + 1)) throw new TypeError("Draft version is out of range.");
  return { ...chat, draft: text, draftVersion: chat.draftVersion + 1 };
}

export function beginSubmission(chat: ChatState, original: UserMessageSubmission): ChatState {
  if (!isSubmission(original)) throw new TypeError("Invalid original submission.");
  return {
    ...chat,
    pending: {
      original: copySubmission(original),
      draftVersion: chat.draftVersion,
      status: "sending",
    },
    error: null,
  };
}

export function applyReceipt(chat: ChatState, sessionId: string, receipt: unknown): ChatState {
  if (!isReceipt(receipt)) throw new TypeError("Invalid message receipt.");
  if (receipt.sessionId !== sessionId) throw new TypeError("Receipt belongs to another session.");
  const pending = chat.pending;
  if (pending === null || !sameSubmission(pending.original, receipt.submission))
    throw new TypeError("Receipt does not match the original submission.");

  const accepted = receipt.acceptance === "accepted";
  const clearDraft =
    accepted && pending.draftVersion === chat.draftVersion && chat.draft === pending.original.text;
  return {
    ...chat,
    draft: clearDraft ? "" : chat.draft,
    pending: null,
    knownExecutionId: receipt.executionId,
    error: receipt.refusalReason === null ? null : refusalErrors[receipt.refusalReason],
  };
}

export function decodeWorkspace(raw: string): WorkspaceState {
  const state: unknown = JSON.parse(raw);
  if (!isWorkspace(state)) throw new TypeError("Invalid saved workspace.");
  return {
    ...state,
    chats: Object.fromEntries(
      Object.entries(state.chats).map(([id, chat]) => [
        id,
        chat.pending === null
          ? chat
          : { ...chat, pending: { ...chat.pending, status: "uncertain" } },
      ]),
    ),
  };
}

export function encodeWorkspace(state: WorkspaceState): string {
  if (!isWorkspace(state)) throw new TypeError("Invalid workspace.");
  return JSON.stringify(state);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isId(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function isRevision(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === "string";
}

function isSelection(value: unknown): value is LlmSelection | null {
  if (value === null) return true;
  if (!isRecord(value) || !isRecord(value.connection)) return false;
  const connection = value.connection;
  return (
    isId(connection.id) &&
    typeof connection.name === "string" &&
    (connection.route === "openAI" ||
      connection.route === "chatGpt" ||
      connection.route === "custom") &&
    (connection.api === "openAIResponses" || connection.api === "openAIChatCompletions") &&
    isId(connection.endpoint) &&
    (connection.authentication === "none" ||
      connection.authentication === "apiKey" ||
      connection.authentication === "oAuth") &&
    isId(value.modelId) &&
    isNullableString(value.reasoningEffort)
  );
}

function isSubmission(value: unknown): value is UserMessageSubmission {
  return (
    isRecord(value) &&
    isId(value.submissionId) &&
    isRevision(value.expectedHistoryRevision) &&
    typeof value.text === "string" &&
    isSelection(value.llmSelection)
  );
}

function isRefusalReason(value: unknown): value is MessageRefusalReason {
  return typeof value === "string" && Object.hasOwn(refusalErrors, value);
}

function isReceipt(value: unknown): value is UserMessageReceipt {
  if (!isRecord(value) || !isId(value.sessionId) || !isSubmission(value.submission)) return false;
  if (value.acceptance === "accepted")
    return value.refusalReason === null && isId(value.executionId);
  return (
    value.acceptance === "refused" &&
    isRefusalReason(value.refusalReason) &&
    value.executionId === null
  );
}

function sameSelection(left: LlmSelection | null, right: LlmSelection | null): boolean {
  if (left === null || right === null) return left === right;
  return (
    left.modelId === right.modelId &&
    left.reasoningEffort === right.reasoningEffort &&
    left.connection.id === right.connection.id &&
    left.connection.name === right.connection.name &&
    left.connection.route === right.connection.route &&
    left.connection.api === right.connection.api &&
    left.connection.endpoint === right.connection.endpoint &&
    left.connection.authentication === right.connection.authentication
  );
}

function sameSubmission(left: UserMessageSubmission, right: UserMessageSubmission): boolean {
  return (
    left.submissionId === right.submissionId &&
    left.expectedHistoryRevision === right.expectedHistoryRevision &&
    left.text === right.text &&
    sameSelection(left.llmSelection, right.llmSelection)
  );
}

function copySubmission(original: UserMessageSubmission): UserMessageSubmission {
  const selection = original.llmSelection;
  return {
    ...original,
    llmSelection:
      selection === null ? null : { ...selection, connection: { ...selection.connection } },
  };
}

function isChat(value: unknown): value is ChatState {
  if (!isRecord(value) || !isRevision(value.draftVersion)) return false;
  const pending = value.pending;
  return (
    typeof value.draft === "string" &&
    isSelection(value.selection) &&
    (value.knownExecutionId === null || isId(value.knownExecutionId)) &&
    typeof value.title === "string" &&
    isNullableString(value.error) &&
    (pending === null ||
      (isRecord(pending) &&
        isSubmission(pending.original) &&
        isRevision(pending.draftVersion) &&
        pending.draftVersion <= value.draftVersion &&
        (pending.status === "sending" || pending.status === "uncertain")))
  );
}

function isWorkspace(value: unknown): value is WorkspaceState {
  if (
    !isRecord(value) ||
    value.version !== 1 ||
    !Array.isArray(value.tabs) ||
    !value.tabs.every(isId) ||
    !isRecord(value.chats) ||
    !isSelection(value.lastSelection)
  )
    return false;
  const tabs: string[] = value.tabs;
  const chats = value.chats;
  return (
    new Set(tabs).size === tabs.length &&
    (value.activeId === null || (isId(value.activeId) && tabs.includes(value.activeId))) &&
    tabs.every((id) => Object.hasOwn(chats, id)) &&
    Object.entries(chats).every(([id, chat]) => isId(id) && isChat(chat))
  );
}
