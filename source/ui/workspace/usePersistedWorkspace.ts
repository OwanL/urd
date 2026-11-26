import { useCallback, useMemo, useRef, useState } from "preact/hooks";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import {
  editDraft as updateDraft,
  appendDraftImage as appendImage,
  removeDraftImage as removeImage,
} from "../chat/ChatState.ts";
import type { UserImage } from "../sessions/transcript/Messages.ts";

import type { ChatState } from "../chat/ChatState.ts";
import { removeWorkspaceSessions, type WorkspaceState } from "./WorkspaceState.ts";
import { errorMessage } from "../http/ErrorMessages.ts";
import type { WorkspaceCommandAccess } from "./WorkspaceCommands.ts";
import type { WorkspacePersistence } from "./WorkspacePersistence.ts";

type StorageIssueKind = "read" | "write" | "cleanup";

export interface WorkspaceStorageIssue {
  kind: StorageIssueKind;
  message: string;
}

export interface PersistedWorkspace {
  state: WorkspaceState;
  loaded: { first: boolean } | null;
  storageIssue: WorkspaceStorageIssue | null;
  access: WorkspaceCommandAccess;
  forgetSessions(ids: ReadonlySet<string>): void;
  isClosed(id: string): boolean;
  editDraft(id: string, text: string): boolean;
  appendDraftImage(id: string, image: UserImage): boolean;
  removeDraftImage(id: string, imageId: string): boolean;
  selectModel(id: string, selection: LlmSelection): boolean;
  retryStorage(): void;
}

// Owns the workspace snapshot and its browser-session persistence lifecycle.
export function usePersistedWorkspace(persistence: WorkspacePersistence): PersistedWorkspace {
  const [initial] = useState(() => persistence.load());
  const [state, setState] = useState(initial.state);
  const current = useRef(initial.state);
  const pendingCleanup = useRef(new Set<string>());
  const closed = useRef(new Set<string>());
  const isClosed = useCallback((id: string) => closed.current.has(id), []);
  const storageProblem = useRef<StorageIssueKind | null>(initial.error ? "read" : null);
  const [loaded, setLoaded] = useState<{ first: boolean } | null>(() =>
    initial.error ? null : { first: initial.first },
  );
  const [storageIssue, setStorageIssue] = useState<WorkspaceStorageIssue | null>(() =>
    initial.error ? { kind: "read", message: initial.error } : null,
  );

  const reportCleanup = useCallback((error: string | null) => {
    if (error) {
      storageProblem.current = "cleanup";
      setStorageIssue({
        kind: "cleanup",
        message: `Workspace was saved, but old chat data could not be cleared: ${error}`,
      });
    } else if (storageProblem.current === "write" || storageProblem.current === "cleanup") {
      storageProblem.current = null;
      setStorageIssue(null);
    }
  }, []);

  const saveWorkspace = useCallback(
    (next: WorkspaceState) => {
      const { cleanupError } = persistence.save(next, pendingCleanup.current);
      if (!cleanupError) pendingCleanup.current.clear();
      reportCleanup(cleanupError);
    },
    [persistence, reportCleanup],
  );

  const save = useCallback(
    (next: WorkspaceState, draftId?: string): boolean => {
      if (storageProblem.current === "read") return false;
      current.current = next;
      setState(next);
      if (storageProblem.current === "write") return false;
      try {
        if (draftId) {
          const chat = next.chats[draftId];
          if (!chat) throw new Error("Draft chat is unavailable.");
          persistence.saveDraft(draftId, chat);
        } else saveWorkspace(next);
        return true;
      } catch (cause) {
        storageProblem.current = "write";
        setStorageIssue({
          kind: "write",
          message: `Workspace could not be saved: ${errorMessage(cause)}`,
        });
        return false;
      }
    },
    [persistence, saveWorkspace],
  );

  const forgetSessions = useCallback(
    (ids: ReadonlySet<string>) => {
      for (const id of ids) {
        closed.current.add(id);
        pendingCleanup.current.add(id);
      }
      save(removeWorkspaceSessions(current.current, ids));
    },
    [save],
  );

  const updateChat = useCallback(
    (
      id: string,
      update: (previous: ChatState) => ChatState,
      draftOnly = false,
      lastSelection = current.current.lastSelection,
    ): boolean => {
      const previous = current.current.chats[id];
      if (!previous) return false;
      return save(
        {
          ...current.current,
          lastSelection,
          chats: { ...current.current.chats, [id]: update(previous) },
        },
        draftOnly ? id : undefined,
      );
    },
    [save],
  );
  const read = useCallback(() => current.current, []);
  const canPersist = useCallback(
    () => storageProblem.current !== "read" && storageProblem.current !== "write",
    [],
  );
  const access = useMemo<WorkspaceCommandAccess>(
    () => ({
      read,
      save: (next) => save(next),
      updateChat: (id, update, lastSelection) => updateChat(id, update, false, lastSelection),
      canPersist,
    }),
    [canPersist, read, save, updateChat],
  );

  const editDraft = useCallback(
    (id: string, text: string) => updateChat(id, (chat) => updateDraft(chat, text), true),
    [updateChat],
  );
  const appendDraftImage = useCallback(
    (id: string, image: UserImage) => updateChat(id, (chat) => appendImage(chat, image), true),
    [updateChat],
  );
  const removeDraftImage = useCallback(
    (id: string, imageId: string) => updateChat(id, (chat) => removeImage(chat, imageId), true),
    [updateChat],
  );
  const selectModel = useCallback(
    (id: string, selection: LlmSelection) =>
      updateChat(id, (chat) => ({ ...chat, selection }), false, selection),
    [updateChat],
  );

  const retryStorage = useCallback(() => {
    if (storageProblem.current === "read") {
      const restored = persistence.load();
      if (restored.error) {
        setStorageIssue({ kind: "read", message: restored.error });
        return;
      }
      current.current = restored.state;
      setState(restored.state);
      setLoaded({ first: restored.first });
      storageProblem.current = null;
      setStorageIssue(null);
      return;
    }
    if (storageProblem.current !== "write" && storageProblem.current !== "cleanup") return;
    try {
      saveWorkspace(current.current);
    } catch (cause) {
      storageProblem.current = "write";
      setStorageIssue({
        kind: "write",
        message: `Workspace could not be saved: ${errorMessage(cause)}`,
      });
    }
  }, [persistence, saveWorkspace]);

  return {
    state,
    loaded,
    storageIssue,
    access,
    forgetSessions,
    isClosed,
    editDraft,
    appendDraftImage,
    removeDraftImage,
    selectModel,
    retryStorage,
  };
}
