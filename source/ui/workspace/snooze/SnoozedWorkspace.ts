import { newChat } from "../../chat/ChatState.ts";
import type { WorkspaceState } from "../WorkspaceState.ts";
import type { SnoozedChat } from "./SnoozeClient.ts";

export function activeVisibleTab(
  tabs: readonly string[],
  active: string | null,
  isSnoozed: (id: string) => boolean,
): string | null {
  if (active === null || !isSnoozed(active)) return active;
  const index = tabs.indexOf(active);
  return (
    tabs.slice(index + 1).find((id) => !isSnoozed(id)) ??
    tabs.slice(0, index).findLast((id) => !isSnoozed(id)) ??
    null
  );
}

// Retain hidden tabs in local order; hiding is not the destructive session-removal path.
export function rememberSnoozedChats(
  state: WorkspaceState,
  snoozed: readonly SnoozedChat[],
  isClosed: (id: string) => boolean,
): WorkspaceState {
  const hidden = new Set(snoozed.map((chat) => chat.sessionId));
  const activeId = activeVisibleTab(state.tabs, state.activeId, (id) => hidden.has(id));
  const knownTabs = new Set(state.tabs);
  const missing = snoozed.filter(
    ({ sessionId }) => !knownTabs.has(sessionId) && !isClosed(sessionId),
  );
  if (!missing.length) return activeId === state.activeId ? state : { ...state, activeId };

  const chats = { ...state.chats };
  for (const { sessionId, title } of missing) {
    chats[sessionId] ??= title
      ? { ...newChat(null), title, titleProvenance: "generated" }
      : newChat(null);
  }
  return {
    ...state,
    activeId,
    chats,
    tabs: [...state.tabs, ...missing.map((chat) => chat.sessionId)],
  };
}
