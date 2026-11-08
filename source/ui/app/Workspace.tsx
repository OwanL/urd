import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Composer } from "../chat/Composer.tsx";
import { ProviderClient } from "../providers/ProviderClient.ts";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
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
} from "./WorkspaceState.ts";
import type { ChatState, WorkspaceState } from "./WorkspaceState.ts";
import { Transcript } from "./Transcript.tsx";
import type { ReadingPosition } from "./Transcript.tsx";
import { useActiveChat } from "./useActiveChat.ts";
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
interface ModelOption {
  key: string;
  modelLabel: string;
  providerLabel: string;
  selection: LlmSelection;
}

function load(): { state: WorkspaceState; error: string | null; first: boolean } {
  try {
    const raw = sessionStorage.getItem(storageKey);
    return {
      state: raw === null ? emptyWorkspace() : decodeWorkspace(raw),
      error: null,
      first: raw === null,
    };
  } catch (cause) {
    return {
      state: emptyWorkspace(),
      error: `Saved workspace could not be read: ${description(cause)}`,
      first: false,
    };
  }
}

export function Workspace() {
  const [initial] = useState(load);
  const [workspace, setWorkspace] = useState(initial.state);
  const current = useRef(workspace);
  const storageBlocked = useRef(initial.error !== null);
  const [globalError, setGlobalError] = useState<string | null>(initial.error);
  const [creating, setCreating] = useState(false);
  const createPending = useRef(false);
  const [ready, setReady] = useState(false);
  const [models, setModels] = useState<ModelOption[]>([]);
  const [modelsError, setModelsError] = useState<string | null>(null);
  const [refreshingModels, setRefreshingModels] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [stopping, setStopping] = useState<
    Record<string, { executionId: string; pending: boolean; error: string | null }>
  >({});
  const checks = useRef(new Set<string>());
  const receiptReads = useRef(new Map<string, AbortController>());
  const [checking, setChecking] = useState<Record<string, boolean>>({});
  const stoppingRequests = useRef(new Set<string>());
  const readingPositions = useRef(new Map<string, ReadingPosition>());
  const activeId = workspace.activeId;
  const chat = activeId ? workspace.chats[activeId] : undefined;

  const save = useCallback((next: WorkspaceState): boolean => {
    current.current = next;
    setWorkspace(next);
    if (storageBlocked.current) return false;
    try {
      sessionStorage.setItem(storageKey, encodeWorkspace(next));
      return true;
    } catch (cause) {
      storageBlocked.current = true;
      setGlobalError(`Workspace could not be saved: ${description(cause)}`);
      return false;
    }
  }, []);
  const changeChat = useCallback(
    (id: string, update: (previous: ChatState) => ChatState): boolean => {
      const state = current.current;
      const previous = state.chats[id];
      if (!previous) return false;
      return save({ ...state, chats: { ...state.chats, [id]: update(previous) } });
    },
    [save],
  );
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
  const create = useCallback(async () => {
    if (storageBlocked.current || createPending.current) return;
    // Persist the first visit before dispatch: reload must not retry an uncertain Create.
    if (!save(current.current)) return;
    createPending.current = true;
    setCreating(true);
    setGlobalError(null);
    try {
      const id = await sessions.create();
      const state = current.current;
      save({
        ...state,
        activeId: id,
        tabs: [...state.tabs, id],
        chats: { ...state.chats, [id]: newChat(state.lastSelection) },
      });
    } catch (cause) {
      setGlobalError(`Chat creation is uncertain: ${description(cause)}`);
    } finally {
      createPending.current = false;
      setCreating(false);
    }
  }, [save]);

  useEffect(() => {
    const controller = new AbortController();
    async function discover() {
      if (initial.error) return;
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
          } else if (page.nextCursor === null) await create();
        }
        setReady(true);
      } catch (cause) {
        if (!controller.signal.aborted) setGlobalError(description(cause));
      }
    }
    void discover();
    return () => controller.abort();
  }, [initial, open, save, create]);
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
        ?.parentElement?.scrollIntoView({ block: "nearest", inline: "nearest" });
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
    async function catalogs() {
      try {
        const saved = await providers.list(controller.signal);
        const options: ModelOption[] = [];
        for (const connection of saved) {
          const catalog = await providers.getModels(connection.id, controller.signal);
          for (const model of catalog.models)
            options.push({
              key: `${connection.id}/${model.id}`,
              modelLabel: model.name,
              providerLabel: connection.name,
              selection: { connection, modelId: model.id, reasoningEffort: null },
            });
        }
        if (!controller.signal.aborted) {
          setModels(options);
          setModelsError(null);
        }
      } catch (cause) {
        if (!controller.signal.aborted) setModelsError(description(cause));
      }
    }
    void catalogs();
    return () => controller.abort();
  }, []);
  async function refreshModels() {
    setRefreshingModels(true);
    setModelsError(null);
    try {
      const saved = await providers.list();
      const options: ModelOption[] = [];
      for (const connection of saved) {
        const catalog = await providers.refreshModels(connection.id);
        for (const model of catalog.models)
          options.push({
            key: `${connection.id}/${model.id}`,
            modelLabel: model.name,
            providerLabel: connection.name,
            selection: { connection, modelId: model.id, reasoningEffort: null },
          });
      }
      setModels(options);
    } catch (cause) {
      setModelsError(description(cause));
    } finally {
      setRefreshingModels(false);
    }
  }

  const observeSnapshot = useCallback(
    (snapshot: SessionSnapshot, knownWhenRead: string | null) => {
      const saved = current.current.chats[snapshot.sessionId];
      if (saved?.title === "New chat" && snapshot.messages.length > 0) {
        const prompt = snapshot.messages.find((message) => message.message.role === "user");
        const title = [...(prompt?.preview.trim() ?? "Chat")].slice(0, 48).join("");
        changeChat(snapshot.sessionId, (previous) => ({ ...previous, title: title || "Chat" }));
      }
      if (
        saved?.knownExecutionId &&
        executionSettled(snapshot, saved.knownExecutionId, knownWhenRead)
      )
        changeChat(snapshot.sessionId, (previous) => ({ ...previous, knownExecutionId: null }));
      setStopping((previous) => {
        const executionId = previous[snapshot.sessionId]?.executionId;
        if (
          !executionId ||
          (snapshot.unsettledExecutionId && snapshot.lastSettlement?.executionId !== executionId)
        )
          return previous;
        const next = { ...previous };
        delete next[snapshot.sessionId];
        return next;
      });
    },
    [changeChat],
  );
  const {
    snapshot,
    error: readError,
    visible,
  } = useActiveChat(
    sessions,
    activeId,
    refresh,
    chat?.knownExecutionId ?? null,
    chat?.pending?.status === "sending",
    observeSnapshot,
  );

  const reconcile = useCallback(
    async (id: string, retry: boolean) => {
      const original = current.current.chats[id]?.pending?.original;
      if (!original || checks.current.has(id) || (retry && storageBlocked.current)) return;
      if (!retry && document.hidden) return;
      const controller = retry ? null : new AbortController();
      if (controller) receiptReads.current.set(id, controller);
      checks.current.add(id);
      setChecking((previous) => ({ ...previous, [id]: true }));
      try {
        if (
          retry &&
          !changeChat(id, (previous) =>
            previous.pending
              ? { ...previous, pending: { ...previous.pending, status: "sending" }, error: null }
              : previous,
          )
        )
          throw new Error("The original Send could not be journaled; it was not dispatched.");
        const receipt = retry
          ? await sessions.submitMessage(id, original)
          : await sessions.getMessageReceipt(id, original.submissionId, controller?.signal);
        if (receipt === null) throw new Error("No receipt found. Send remains uncertain.");
        changeChat(id, (previous) => applyReceipt(previous, id, receipt));
      } catch (cause) {
        if (controller?.signal.aborted) return;
        changeChat(id, (previous) =>
          previous.pending
            ? {
                ...previous,
                pending: { ...previous.pending, status: "uncertain" },
                error: description(cause),
              }
            : previous,
        );
      } finally {
        checks.current.delete(id);
        receiptReads.current.delete(id);
        setChecking((previous) => {
          const next = { ...previous };
          delete next[id];
          return next;
        });
        setRefresh((value) => value + 1);
      }
    },
    [changeChat],
  );
  useEffect(() => {
    const reads = receiptReads.current;
    function checkPending() {
      if (document.hidden) {
        for (const controller of reads.values()) controller.abort();
        return;
      }
      for (const [id, saved] of Object.entries(current.current.chats))
        if (saved.pending?.status === "uncertain") void reconcile(id, false);
    }
    checkPending();
    window.addEventListener("focus", checkPending);
    document.addEventListener("visibilitychange", checkPending);
    return () => {
      for (const controller of reads.values()) controller.abort();
      window.removeEventListener("focus", checkPending);
      document.removeEventListener("visibilitychange", checkPending);
    };
  }, [reconcile]);

  const active = Boolean(snapshot?.unsettledExecutionId && !snapshot.admissionFenced);
  const stopState = activeId ? stopping[activeId] : undefined;
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
    !storageBlocked.current,
  );

  async function send() {
    if (!canSend || !chat || !activeId || !snapshot || !selection) return;
    const id = activeId;
    const latest = current.current.chats[id];
    const initiatingSelection = active
      ? snapshot.latestLlmSelection
      : (latest?.selection ?? snapshot.latestLlmSelection);
    if (
      !latest ||
      latest.pending ||
      !initiatingSelection ||
      !latest.draft.trim() ||
      submissionTextError(latest.draft)
    )
      return;
    const original = {
      submissionId: crypto.randomUUID(),
      expectedHistoryRevision: snapshot.historyRevision,
      text: latest.draft,
      llmSelection: initiatingSelection,
    };
    const persisted = changeChat(id, (previous) => ({
      ...beginSubmission(previous, original),
      title:
        previous.title === "New chat"
          ? [...original.text.trim()].slice(0, 48).join("")
          : previous.title,
    }));
    if (!persisted) {
      changeChat(id, (previous) =>
        previous.pending
          ? {
              ...previous,
              pending: { ...previous.pending, status: "uncertain" },
              error: "Send was not dispatched because the journal could not be saved.",
            }
          : previous,
      );
      return;
    }
    await reconcile(id, true);
  }
  async function stop() {
    const id = activeId;
    const executionId = snapshot?.unsettledExecutionId;
    if (!id || !executionId || stopId) return;
    const requestKey = `${id}/${executionId}`;
    if (stoppingRequests.current.has(requestKey)) return;
    stoppingRequests.current.add(requestKey);
    setStopping((previous) => ({
      ...previous,
      [id]: { executionId, pending: true, error: null },
    }));
    try {
      const receipt = await sessions.stopExecution(id, executionId);
      if (
        receipt.executionId !== executionId ||
        (receipt.status !== "requested" && receipt.status !== "settled")
      )
        throw new Error("Stop was not acknowledged.");
    } catch (cause) {
      setStopping((previous) => {
        if (previous[id]?.executionId !== executionId) return previous;
        return {
          ...previous,
          [id]: {
            executionId,
            pending: false,
            error: `Stop acknowledgement uncertain: ${description(cause)}`,
          },
        };
      });
    } finally {
      stoppingRequests.current.delete(requestKey);
      setRefresh((value) => value + 1);
    }
  }
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
        <div
          class="chat-tabs"
          role="tablist"
          aria-label="Chats"
          onKeyDown={(event) => {
            if (
              !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) ||
              !workspace.tabs.length
            )
              return;
            event.preventDefault();
            const index = workspace.tabs.indexOf(activeId ?? "");
            const next =
              event.key === "Home"
                ? 0
                : event.key === "End"
                  ? workspace.tabs.length - 1
                  : (index + (event.key === "ArrowLeft" ? -1 : 1) + workspace.tabs.length) %
                    workspace.tabs.length;
            const id = workspace.tabs[next];
            if (id) {
              open(id);
              requestAnimationFrame(() => document.getElementById(`tab-${id}`)?.focus());
            }
          }}
        >
          {workspace.tabs.map((id) => (
            <div class={`chat-tab ${id === activeId ? "is-active" : ""}`} key={id}>
              <button
                id={`tab-${id}`}
                role="tab"
                aria-selected={id === activeId}
                aria-controls="chat-view"
                tabIndex={id === activeId ? 0 : -1}
                onClick={() => open(id)}
                onKeyDown={(event) => {
                  if (event.key === "Delete") close(id);
                }}
              >
                {workspace.chats[id]?.title ?? "Chat"}
              </button>
              <button
                class="close-tab"
                aria-label={`Close ${workspace.chats[id]?.title ?? "chat"}`}
                onClick={() => close(id)}
              >
                ×
              </button>
            </div>
          ))}
        </div>
        <button
          class="icon-button new-chat"
          aria-label="New chat"
          disabled={creating || !ready || storageBlocked.current}
          aria-busy={creating}
          onClick={() => {
            void create();
          }}
        >
          {creating ? "…" : "+"}
        </button>
        <div class="top-actions">
          <button class="icon-button" aria-label="Snooze">
            ☾
          </button>
          <button class="icon-button" aria-label="Settings">
            ⚙
          </button>
        </div>
      </header>
      {globalError && (
        <div class="workspace-error" role="alert">
          {globalError}
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
                  : checking[activeId]
                    ? "Checking receipt…"
                    : "Send uncertain."}{" "}
                {chat.pending.status === "uncertain" && (
                  <>
                    <button
                      disabled={checking[activeId]}
                      onClick={() => {
                        void reconcile(activeId, false);
                      }}
                    >
                      Check receipt
                    </button>
                    <button
                      disabled={storageBlocked.current || checking[activeId]}
                      onClick={() => {
                        void reconcile(activeId, true);
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
              changeChat(activeId, (previous) => editDraft(previous, text));
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
              void send();
            }}
            onStop={() => {
              void stop();
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
