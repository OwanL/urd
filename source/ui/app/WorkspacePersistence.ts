import { decodeWorkspace, emptyWorkspace, encodeWorkspace } from "./WorkspaceState.ts";
import type { ChatState, WorkspaceState } from "./WorkspaceState.ts";

export interface WorkspaceLoad {
  state: WorkspaceState;
  first: boolean;
  error: string | null;
}

interface SavedDraft {
  draft: string;
  draftVersion: number;
}

// Browser-session persistence keeps ordinary draft writes separate from command journals.
export class WorkspacePersistence {
  private readonly storage: () => Storage;
  private readonly key: string;

  constructor(storage: () => Storage, key: string) {
    this.storage = storage;
    this.key = key;
  }

  load(): WorkspaceLoad {
    try {
      const storage = this.storage();
      const raw = storage.getItem(this.key);
      if (raw === null) return { state: emptyWorkspace(), first: true, error: null };

      const state = decodeWorkspace(raw);
      const chats = { ...state.chats };
      for (const [id, chat] of Object.entries(chats)) {
        const saved = storage.getItem(this.draftKey(id));
        if (saved === null) continue;
        const draft = decodeDraft(saved);
        if (draft.draftVersion > chat.draftVersion)
          chats[id] = { ...chat, draft: draft.draft, draftVersion: draft.draftVersion };
      }
      return { state: { ...state, chats }, first: false, error: null };
    } catch (cause) {
      return {
        state: emptyWorkspace(),
        first: false,
        error: `Saved workspace could not be read: ${description(cause)}`,
      };
    }
  }

  save(state: WorkspaceState): void {
    this.storage().setItem(this.key, encodeWorkspace(state));
  }

  saveDraft(id: string, chat: ChatState): void {
    const draft: SavedDraft = { draft: chat.draft, draftVersion: chat.draftVersion };
    this.storage().setItem(this.draftKey(id), JSON.stringify(draft));
  }

  private draftKey(id: string): string {
    return `${this.key}.draft.${encodeURIComponent(id)}`;
  }
}

function decodeDraft(raw: string): SavedDraft {
  const value: unknown = JSON.parse(raw);
  if (
    !isRecord(value) ||
    !Object.hasOwn(value, "draft") ||
    typeof value.draft !== "string" ||
    !Object.hasOwn(value, "draftVersion") ||
    typeof value.draftVersion !== "number" ||
    !Number.isSafeInteger(value.draftVersion) ||
    value.draftVersion < 0
  )
    throw new TypeError("Invalid saved draft.");
  return { draft: value.draft, draftVersion: value.draftVersion };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
