import type { ChatState } from "../ChatState.ts";

export interface MessageEditingAccess {
  read(): { chats: Record<string, ChatState> };
  updateChat(id: string, update: (previous: ChatState) => ChatState): boolean;
  canPersist(): boolean;
}
