import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Composer } from "../chat/Composer.tsx";
import { IconButton } from "../components/IconButton.tsx";
import { Icon } from "../components/Icon.tsx";
import { Settings } from "../settings/Settings.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { editDraft, newChat, submissionTextError } from "./WorkspaceState.ts";
import type { ChatState, WorkspaceState } from "./WorkspaceState.ts";
import { WorkspacePersistence } from "./WorkspacePersistence.ts";
import { WorkspaceModels } from "./WorkspaceModels.ts";
import { emptyWorkspaceCommandState, WorkspaceCommands } from "./WorkspaceCommands.ts";
import type { WorkspaceCommandState } from "./WorkspaceCommands.ts";
import type { ModelOption } from "./WorkspaceModels.ts";
import { ChatTabs } from "./ChatTabs.tsx";
import { moveTab } from "./TabOrder.ts";
import { Transcript } from "./Transcript.tsx";
import type { ReadingPosition } from "./Transcript.tsx";
import { useSessionSnapshot } from "./useSessionSnapshot.ts";
import "./workspace.css";

const sessions = new SessionClient(new URL(window.location.href));
const providers = new ProviderClient(new URL(window.location.href));
const storageKey = "urd.workspace.v1";
const efforts = ["none", "low", "medium", "high", "extraHigh"];
const idPattern = /^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i;
function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
function modelKey(selection: LlmSelection): string {
  return `${selection.connection.id}/${selection.modelId}`;
}
export function Workspace() {
  const [persistence] = useState(() => new WorkspacePersistence(() => sessionStorage, storageKey));
  const [initial] = useState(() => persistence.load());
  const [workspace, setWorkspace] = useState(initial.state);
  const current = useRef(workspace);
  const storageProblem = useRef<"read" | "write" | "cleanup" | null>(initial.error ? "read" : null);
  const [storageError, setStorageError] = useState<string | null>(initial.error);
  const [readRecovered, setReadRecovered] = useState(false);
  const [globalError, setGlobalError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsAnchor = useRef<HTMLButtonElement>(null);
  const [catalogRevision, setCatalogRevision] = useState(0);
  const [modelCatalog] = useState(() => new WorkspaceModels(providers));
  const [models, setModels] = useState<ModelOption[]>([]);
  const [modelsError, setModelsError] = useState<string | null>(null);
  const [refreshingModels, setRefreshingModels] = useState(false);
  const providersChanged = useCallback(() => {
    modelCatalog.invalidate();
    setCatalogRevision((value) => value + 1);
    setRefreshingModels(false);
    setModelsError(null);
  }, [modelCatalog]);
  const [refresh, setRefresh] = useState(0);
  const [commandState, setCommandState] = useState<WorkspaceCommandState>(
    emptyWorkspaceCommandState,
  );
  const readingPositions = useRef(new Map<string, ReadingPosition>());
  const activeId = workspace.activeId;
  const chat = activeId ? workspace.chats[activeId] : undefined;

  const reportCleanup = useCallback((error: string | null) => {
    if (error) {
      storageProblem.current = "cleanup";
      setStorageError(`Workspace was saved, but old draft data could not be cleared: ${error}`);
    } else if (storageProblem.current === "write" || storageProblem.current === "cleanup") {
      storageProblem.current = null;
      setStorageError(null);
    }
  }, []);
  const save = useCallback(
    (next: WorkspaceState, draftId?: string): boolean => {
      if (storageProblem.current === "read") return false;
      current.current = next;
      setWorkspace(next);
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
        setStorageError(`Workspace could not be saved: ${description(cause)}`);
        return false;
      }
    },
    [persistence, reportCleanup],
  );
  const updateChat = useCallback(
    (
      id: string,
      update: (previous: ChatState) => ChatState,
      persistenceKind: "workspace" | "draft" = "workspace",
    ): boolean => {
      const state = current.current;
      const previous = state.chats[id];
      if (!previous) return false;
      return save(
        { ...state, chats: { ...state.chats, [id]: update(previous) } },
        persistenceKind === "draft" ? id : undefined,
      );
    },
    [save],
  );
  const readWorkspace = useCallback(() => current.current, []);
  const canPersist = useCallback(
    () => storageProblem.current !== "read" && storageProblem.current !== "write",
    [],
  );
  const refreshSnapshot = useCallback(() => setRefresh((value) => value + 1), []);
  const [commands] = useState(
    () =>
      new WorkspaceCommands(
        sessions,
        { read: readWorkspace, save, updateChat, canPersist },
        setCommandState,
        setGlobalError,
        refreshSnapshot,
      ),
  );
  const retryStorage = useCallback(() => {
    if (storageProblem.current === "read") {
      const restored = persistence.load();
      if (restored.error) {
        setStorageError(restored.error);
        return;
      }
      current.current = restored.state;
      setWorkspace(restored.state);
      setReadRecovered(true);
      storageProblem.current = null;
      setStorageError(null);
      return;
    }
    if (storageProblem.current !== "write" && storageProblem.current !== "cleanup") return;
    try {
      reportCleanup(persistence.save(current.current).cleanupError);
    } catch (cause) {
      storageProblem.current = "write";
      setStorageError(`Workspace could not be saved: ${description(cause)}`);
    }
  }, [persistence, reportCleanup]);
  const open = useCallback(
    (id: string) => {
      const state = current.current;
      save({
        ...state,
        activeId: id,
        tabs: state.tabs.includes(id) ? state.tabs : [...state.tabs, id],
        chats: { ...state.chats, [id]: state.chats[id] ?? newChat(null) },
      });
    },
    [save],
  );
  useEffect(() => {
    const controller = new AbortController();
    async function discover() {
      if (storageProblem.current === "read") return;
      try {
        const page = await sessions.list(undefined, controller.signal);
        if (controller.signal.aborted) return;
        const hash = window.location.hash.slice(1);
        if (idPattern.test(hash)) open(hash);
        else if (initial.first) {
          if (page.sessionIds.length > 0) {
            const state = current.current;
            const ids = page.sessionIds;
            save({
              ...state,
              tabs: [...ids],
              activeId: ids[0] ?? null,
              chats: Object.fromEntries(ids.map((id) => [id, newChat(null)])),
            });
          } else if (page.nextCursor === null) await commands.create();
        }
        setReady(true);
      } catch (cause) {
        if (!controller.signal.aborted) setGlobalError(description(cause));
      }
    }
    void discover();
    return () => controller.abort();
  }, [initial, open, save, commands, readRecovered]);
  useEffect(() => {
    if (!ready) return;
    window.history.replaceState(
      null,
      "",
      `${window.location.pathname}${window.location.search}${activeId ? `#${activeId}` : ""}`,
    );
  }, [activeId, ready]);
  useLayoutEffect(() => {
    if (activeId)
      document
        .getElementById(`tab-${activeId}`)
        ?.closest(".chat-tab")
        ?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [activeId, chat?.title]);
  useEffect(() => {
    function hashChanged() {
      const id = window.location.hash.slice(1);
      if (idPattern.test(id)) open(id);
    }
    window.addEventListener("hashchange", hashChanged);
    return () => window.removeEventListener("hashchange", hashChanged);
  }, [open]);

  useEffect(() => {
    const controller = new AbortController();
    void modelCatalog.readCached(controller.signal).then((result) => {
      if (!modelCatalog.isCurrent(result.revision)) return;
      if (result.error) {
        setModelsError(description(result.error));
        return;
      }
      setModels(result.options ?? []);
      setModelsError(null);
    });
    return () => {
      controller.abort();
      modelCatalog.invalidate();
    };
  }, [catalogRevision, modelCatalog]);
  async function refreshModels() {
    setRefreshingModels(true);
    setModelsError(null);
    const result = await modelCatalog.refresh();
    if (!modelCatalog.isCurrent(result.revision)) return;
    if (result.error) setModelsError(description(result.error));
    else setModels(result.options ?? []);
    setRefreshingModels(false);
  }

  const observeSnapshot = useCallback(
    (snapshot: SessionSnapshot, knownWhenRead: string | null) =>
      commands.observeSnapshot(snapshot, knownWhenRead),
    [commands],
  );
  const {
    snapshot,
    error: readError,
    visible,
  } = useSessionSnapshot(
    sessions,
    activeId,
    refresh,
    chat?.knownExecutionId ?? null,
    chat?.pending?.status === "sending",
    observeSnapshot,
  );

  useEffect(() => commands.watchPendingReceipts(), [commands]);

  const active = Boolean(snapshot?.unsettledExecutionId && !snapshot.admissionFenced);
  const stopState = activeId ? commandState.stopping[activeId] : undefined;
  const stopId = stopState?.pending ? stopState.executionId : undefined;
  const locked = Boolean(
    active ||
    chat?.pending ||
    stopId ||
    snapshot?.stopRequested ||
    snapshot?.admissionFenced ||
    chat?.knownExecutionId,
  );
  const selection = active
    ? (snapshot?.latestLlmSelection ?? null)
    : (chat?.selection ?? snapshot?.latestLlmSelection ?? null);
  const textError = chat ? submissionTextError(chat.draft) : null;
  const canSend = Boolean(
    ready &&
    chat &&
    snapshot &&
    selection &&
    chat.draft.trim() &&
    !textError &&
    !chat.pending &&
    (!chat.knownExecutionId ||
      snapshot.unsettledExecutionId === chat.knownExecutionId ||
      snapshot.lastSettlement?.executionId === chat.knownExecutionId) &&
    !snapshot.stopRequested &&
    !snapshot.admissionFenced &&
    !stopId &&
    storageProblem.current !== "read" &&
    storageProblem.current !== "write",
  );

  function choose(next: LlmSelection) {
    if (!activeId || locked) return;
    const state = current.current;
    save({
      ...state,
      lastSelection: next,
      chats: { ...state.chats, [activeId]: { ...state.chats[activeId]!, selection: next } },
    });
  }
  function close(id: string) {
    const state = current.current;
    const index = state.tabs.indexOf(id);
    const tabs = state.tabs.filter((tab) => tab !== id);
    save({
      ...state,
      tabs,
      activeId:
        state.activeId === id ? (tabs[Math.min(index, tabs.length - 1)] ?? null) : state.activeId,
    });
    requestAnimationFrame(() =>
      document
        .querySelector<HTMLButtonElement>("[role=tab][aria-selected=true], .new-chat")
        ?.focus(),
    );
  }

  return (
    <main class="workspace">
      <header class="top-strip">
        <ChatTabs
          tabs={workspace.tabs.map((id) => ({ id, title: workspace.chats[id]?.title ?? "Chat" }))}
          activeId={activeId}
          onOpen={open}
          onClose={close}
          onReorder={(id, beforeId) => {
            const state = current.current;
            const ordered = moveTab(
              state.tabs.map((id) => ({ id })),
              id,
              beforeId,
            );
            save({ ...state, tabs: ordered.map((tab) => tab.id) });
          }}
        />
        <IconButton
          icon="plus"
          class="new-chat"
          label="New chat"
          hint={commandState.creating ? "Creating chat…" : "New chat"}
          placement="below"
          disabled={
            commandState.creating ||
            !ready ||
            storageProblem.current === "read" ||
            storageProblem.current === "write"
          }
          aria-busy={commandState.creating}
          onClick={() => {
            void commands.create();
          }}
        />
        <div class="top-actions">
          <IconButton
            icon="moon"
            label="Snooze"
            hint={
              <>
                Snooze<span class="tooltip-detail">Not available yet</span>
              </>
            }
            placement="below"
            aria-disabled="true"
          />
          <Tooltip content="Settings" placement="below" disabled={settingsOpen}>
            <button
              ref={settingsAnchor}
              type="button"
              class="icon-button"
              aria-label="Settings"
              aria-haspopup="dialog"
              aria-expanded={settingsOpen}
              onClick={() => setSettingsOpen((value) => !value)}
            >
              <Icon name="settings" />
            </button>
          </Tooltip>
        </div>
      </header>
      <Settings
        open={settingsOpen}
        anchor={settingsAnchor}
        client={providers}
        onClose={() => setSettingsOpen(false)}
        onChanged={providersChanged}
      />
      {globalError && (
        <div class="workspace-error" role="alert">
          {globalError}
        </div>
      )}
      {storageError && (
        <div class="workspace-error" role="alert">
          {storageError}{" "}
          <button type="button" onClick={retryStorage}>
            {storageProblem.current === "read"
              ? "Retry read"
              : storageProblem.current === "cleanup"
                ? "Retry cleanup"
                : "Retry save"}
          </button>
        </div>
      )}
      {activeId && chat ? (
        <section
          id="chat-view"
          class="chat-view"
          role="tabpanel"
          aria-labelledby={`tab-${activeId}`}
        >
          <Transcript
            key={activeId}
            client={sessions}
            snapshot={snapshot}
            sessionId={activeId}
            visible={visible}
            position={readingPositions.current.get(activeId)}
            onPositionChange={(position) => {
              readingPositions.current.set(activeId, position);
            }}
          />
          <div class="chat-status" aria-live="polite">
            {(readError || chat.error || stopState?.error) && (
              <span class="inline-error">{readError || chat.error || stopState?.error} </span>
            )}
            {readError && <button onClick={() => setRefresh((value) => value + 1)}>Refresh</button>}
            {snapshot?.admissionFenced && <span>Session admission is fenced. </span>}
            {snapshot?.unsettledExecutionId && snapshot.admissionFenced && (
              <span>Unsettled execution. </span>
            )}
            {active && (snapshot?.stopRequested || stopId) ? (
              <span>Stopping… </span>
            ) : active ? (
              <span>Running… </span>
            ) : chat.knownExecutionId ? (
              <span>Accepted; awaiting session update. </span>
            ) : null}
            {!active && snapshot?.stopRequested && <span>Stop requested. </span>}
            {!active &&
              !chat.knownExecutionId &&
              !stopId &&
              !snapshot?.stopRequested &&
              snapshot?.lastSettlement?.outcome === "failed" && (
                <span class="inline-error">Execution failed. </span>
              )}
            {!active &&
              !chat.knownExecutionId &&
              !stopId &&
              !snapshot?.stopRequested &&
              snapshot?.lastSettlement?.outcome === "cancelled" && <span>Cancelled. </span>}
            {chat.pending && (
              <span>
                {chat.pending.status === "sending"
                  ? "Sending…"
                  : commandState.checking[activeId]
                    ? "Checking receipt…"
                    : "Send uncertain."}{" "}
                {chat.pending.status === "uncertain" && (
                  <>
                    <button
                      disabled={commandState.checking[activeId]}
                      onClick={() => {
                        void commands.reconcile(activeId, false);
                      }}
                    >
                      Check receipt
                    </button>
                    <button
                      disabled={
                        storageProblem.current === "read" ||
                        storageProblem.current === "write" ||
                        commandState.checking[activeId]
                      }
                      onClick={() => {
                        void commands.reconcile(activeId, true);
                      }}
                    >
                      Retry original
                    </button>
                  </>
                )}
              </span>
            )}
            {textError && <span class="inline-error">{textError}</span>}
          </div>
          <Composer
            text={chat.draft}
            onTextChange={(text) => {
              updateChat(activeId, (previous) => editDraft(previous, text), "draft");
            }}
            models={models}
            modelKey={selection ? modelKey(selection) : null}
            modelLabel={
              selection
                ? (models.find((model) => model.key === modelKey(selection))?.modelLabel ??
                  selection.modelId)
                : "Select model"
            }
            onModelChange={(key) => {
              const option = models.find((model) => model.key === key);
              if (option)
                choose({
                  ...option.selection,
                  reasoningEffort: selection?.reasoningEffort ?? null,
                });
            }}
            reasoningEffort={selection?.reasoningEffort ?? null}
            reasoningOptions={efforts}
            onReasoningChange={(effort) => {
              if (selection) choose({ ...selection, reasoningEffort: effort });
            }}
            selectionLocked={locked}
            canSend={canSend}
            working={active}
            stopping={active && Boolean(stopId || snapshot?.stopRequested)}
            onSend={() => {
              if (activeId && snapshot) void commands.send(activeId, snapshot, active, canSend);
            }}
            onStop={() => {
              if (activeId && snapshot) void commands.stop(activeId, snapshot, stopId);
            }}
            onRefreshModels={() => {
              void refreshModels();
            }}
            refreshingModels={refreshingModels}
            modelsError={modelsError}
          />
        </section>
      ) : (
        <div class="empty-workspace" />
      )}
    </main>
  );
}
