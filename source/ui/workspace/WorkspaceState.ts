import type { LlmSelection } from "../providers/LlmSelection.ts";
import { isChat, isId, isRecord, isSelection, newChat } from "../chat/ChatState.ts";
import type { ChatState } from "../chat/ChatState.ts";

export interface WorkspaceState {
  version: 8;
  tabs: string[];
  activeId: string | null;
  chats: Record<string, ChatState>;
  lastSelection: LlmSelection | null;
}

export function emptyWorkspace(): WorkspaceState {
  return { version: 8, tabs: [], activeId: null, chats: {}, lastSelection: null };
}

export function mergeWorkspaceSessions(
  state: WorkspaceState,
  sessions: Iterable<string>,
): WorkspaceState {
  const known = new Set(state.tabs);
  const added: string[] = [];
  for (const id of sessions) {
    if (known.has(id)) continue;
    known.add(id);
    added.push(id);
  }
  if (!added.length) return state;

  const newChats = Object.fromEntries(
    added.filter((id) => !Object.hasOwn(state.chats, id)).map((id) => [id, newChat(null)]),
  );
  return { ...state, tabs: [...state.tabs, ...added], chats: { ...state.chats, ...newChats } };
}

export function applySessionTitle(
  state: WorkspaceState,
  sessionId: string,
  title: string | null,
): WorkspaceState {
  const chat = state.chats[sessionId];
  if (title === null || !chat || (chat.title === title && chat.titleProvenance === "generated"))
    return state;
  return {
    ...state,
    chats: { ...state.chats, [sessionId]: { ...chat, title, titleProvenance: "generated" } },
  };
}

export function removeWorkspaceSessions(
  state: WorkspaceState,
  removed: ReadonlySet<string>,
): WorkspaceState {
  if (![...removed].some((id) => Object.hasOwn(state.chats, id))) return state;
  const chats = { ...state.chats };
  for (const id of removed) delete chats[id];
  const tabs = state.tabs.filter((id) => !removed.has(id));
  let activeId = state.activeId;
  if (activeId && removed.has(activeId)) {
    const index = state.tabs.indexOf(activeId);
    activeId = state.tabs.slice(index + 1).find((id) => !removed.has(id)) ?? tabs.at(-1) ?? null;
  }
  return { ...state, chats, tabs, activeId };
}

export function decodeWorkspace(raw: string): WorkspaceState {
  const state: unknown = JSON.parse(raw);
  if (!isWorkspace(state)) throw new TypeError("Invalid saved workspace.");
  return {
    ...state,
    chats: Object.fromEntries(
      Object.entries(state.chats).map(([id, chat]) => [
        id,
        {
          ...chat,
          pending: chat.pending === null ? null : { ...chat.pending, status: "uncertain" },
          pendingContinuation:
            chat.pendingContinuation === null
              ? null
              : { ...chat.pendingContinuation, status: "uncertain" },
          messageEdit:
            chat.messageEdit?.status === "loading"
              ? undefined
              : chat.messageEdit
                ? {
                    ...chat.messageEdit,
                    status: chat.messageEdit.submission ? "uncertain" : "editing",
                  }
                : undefined,
        },
      ]),
    ),
  };
}

export function encodeWorkspace(state: WorkspaceState): string {
  if (!isWorkspace(state)) throw new TypeError("Invalid workspace.");
  return JSON.stringify(state);
}

function isWorkspace(value: unknown): value is WorkspaceState {
  if (
    !isRecord(value) ||
    value.version !== 8 ||
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
