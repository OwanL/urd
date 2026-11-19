import { useCallback, useMemo, useRef, useState } from "preact/hooks";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import { editDraft as updateDraft } from "./WorkspaceState.ts";
import type { ChatState, WorkspaceState } from "./WorkspaceState.ts";
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
  editDraft(id: string, text: string): boolean;
  selectModel(id: string, selection: LlmSelection): boolean;
  retryStorage(): void;
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}

// Owns the workspace snapshot and its browser-session persistence lifecycle.
export function usePersistedWorkspace(persistence: WorkspacePersistence): PersistedWorkspace {
  const [initial] = useState(() => persistence.load());
  const [state, setState] = useState(initial.state);
  const current = useRef(initial.state);
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
        message: `Workspace was saved, but old draft data could not be cleared: ${error}`,
      });
    } else if (storageProblem.current === "write" || storageProblem.current === "cleanup") {
      storageProblem.current = null;
      setStorageIssue(null);
    }
  }, []);

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
        } else reportCleanup(persistence.save(next).cleanupError);
        return true;
      } catch (cause) {
        storageProblem.current = "write";
        setStorageIssue({
          kind: "write",
          message: `Workspace could not be saved: ${description(cause)}`,
        });
        return false;
      }
    },
    [persistence, reportCleanup],
  );

  const updateChat = useCallback(
    (id: string, update: (previous: ChatState) => ChatState, draftOnly = false): boolean => {
      const previous = current.current.chats[id];
      if (!previous) return false;
      return save(
        { ...current.current, chats: { ...current.current.chats, [id]: update(previous) } },
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
      updateChat: (id, update) => updateChat(id, update),
      canPersist,
    }),
    [canPersist, read, save, updateChat],
  );

  const editDraft = useCallback(
    (id: string, text: string) => updateChat(id, (chat) => updateDraft(chat, text), true),
    [updateChat],
  );
  const selectModel = useCallback(
    (id: string, selection: LlmSelection) => {
      const workspace = current.current;
      const chat = workspace.chats[id];
      if (!chat) return false;
      return save({
        ...workspace,
        lastSelection: selection,
        chats: { ...workspace.chats, [id]: { ...chat, selection } },
      });
    },
    [save],
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
      reportCleanup(persistence.save(current.current).cleanupError);
    } catch (cause) {
      storageProblem.current = "write";
      setStorageIssue({
        kind: "write",
        message: `Workspace could not be saved: ${description(cause)}`,
      });
    }
  }, [persistence, reportCleanup]);

  return { state, loaded, storageIssue, access, editDraft, selectModel, retryStorage };
}
