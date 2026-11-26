import { isUserImage } from "../chat/ChatState.ts";
import type { UserImage } from "../sessions/transcript/Messages.ts";
import { decodeWorkspace, emptyWorkspace, encodeWorkspace } from "./WorkspaceState.ts";
import { errorMessage } from "../http/ErrorMessages.ts";
import type { ChatState } from "../chat/ChatState.ts";
import type { WorkspaceState } from "./WorkspaceState.ts";

export interface WorkspaceLoad {
  state: WorkspaceState;
  first: boolean;
  error: string | null;
}

interface SavedDraft {
  draft: string;
  draftVersion: number;
  draftImages: readonly UserImage[];
  draftImagesVersion: number;
}

// Browser-session persistence keeps ordinary draft writes separate from command journals.
export class WorkspacePersistence {
  private readonly storage: () => Storage;
  private readonly key: string;
  private readonly clearTranscriptNavigation: (id: string) => void;

  constructor(
    storage: () => Storage,
    key: string,
    clearTranscriptNavigation: (id: string) => void = () => {},
  ) {
    this.storage = storage;
    this.key = key;
    this.clearTranscriptNavigation = clearTranscriptNavigation;
  }

  load(): WorkspaceLoad {
    try {
      const storage = this.storage();
      const raw = storage.getItem(this.key);
      if (raw === null) return { state: emptyWorkspace(), first: true, error: null };

      let state: WorkspaceState;
      try {
        state = decodeWorkspace(raw);
      } catch {
        this.discard(storage);
        return { state: emptyWorkspace(), first: true, error: null };
      }

      const chats = { ...state.chats };
      for (const [id, chat] of Object.entries(chats)) {
        const saved = storage.getItem(this.draftKey(id));
        if (saved === null) continue;
        let draft: SavedDraft;
        try {
          draft = decodeDraft(saved);
        } catch {
          this.discard(storage);
          return { state: emptyWorkspace(), first: true, error: null };
        }
        chats[id] = {
          ...chat,
          ...(draft.draftVersion > chat.draftVersion
            ? { draft: draft.draft, draftVersion: draft.draftVersion }
            : {}),
          ...(draft.draftImagesVersion > chat.draftImagesVersion
            ? { draftImages: draft.draftImages, draftImagesVersion: draft.draftImagesVersion }
            : {}),
        };
      }
      return { state: { ...state, chats }, first: false, error: null };
    } catch (cause) {
      return {
        state: emptyWorkspace(),
        first: false,
        error: `Saved workspace could not be read: ${errorMessage(cause)}`,
      };
    }
  }

  save(
    state: WorkspaceState,
    removedSessions: ReadonlySet<string> = new Set(),
  ): { cleanupError: string | null } {
    const storage = this.storage();
    storage.setItem(this.key, encodeWorkspace(state));
    let cleanupError: string | null = null;
    for (const [id, chat] of Object.entries(state.chats)) {
      try {
        const saved = storage.getItem(this.draftKey(id));
        if (saved === null) continue;
        const draft = decodeDraft(saved);
        if (
          draft.draftVersion <= chat.draftVersion &&
          draft.draftImagesVersion <= chat.draftImagesVersion
        )
          storage.removeItem(this.draftKey(id));
      } catch (cause) {
        cleanupError = errorMessage(cause);
      }
    }
    for (const id of removedSessions) {
      if (Object.hasOwn(state.chats, id)) continue;
      try {
        storage.removeItem(this.draftKey(id));
      } catch (cause) {
        cleanupError = errorMessage(cause);
      }
      try {
        this.clearTranscriptNavigation(id);
      } catch (cause) {
        cleanupError = errorMessage(cause);
      }
    }
    return { cleanupError };
  }

  saveDraft(id: string, chat: ChatState): void {
    const draft: SavedDraft = {
      draft: chat.draft,
      draftVersion: chat.draftVersion,
      draftImages: chat.draftImages,
      draftImagesVersion: chat.draftImagesVersion,
    };
    this.storage().setItem(this.draftKey(id), JSON.stringify(draft));
  }

  private discard(storage: Storage): void {
    const draftPrefix = `${this.key}.draft.`;
    const draftKeys: string[] = [];
    for (let index = 0; index < storage.length; index++) {
      const candidate = storage.key(index);
      if (candidate?.startsWith(draftPrefix)) draftKeys.push(candidate);
    }
    for (const draftKey of draftKeys) storage.removeItem(draftKey);
    storage.removeItem(this.key);
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
    value.draftVersion < 0 ||
    !Array.isArray(value.draftImages) ||
    !value.draftImages.every(isUserImage) ||
    new Set(value.draftImages.map((image) => image.imageId)).size !== value.draftImages.length ||
    typeof value.draftImagesVersion !== "number" ||
    !Number.isSafeInteger(value.draftImagesVersion) ||
    value.draftImagesVersion < 0
  )
    throw new TypeError("Invalid saved draft.");
  return {
    draft: value.draft,
    draftVersion: value.draftVersion,
    draftImages: value.draftImages,
    draftImagesVersion: value.draftImagesVersion,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
