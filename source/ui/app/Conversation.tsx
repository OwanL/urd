import { useCallback, useRef, useState } from "preact/hooks";
import { MessageEditingContext, InlineMessageEditor } from "./EditableMessage.tsx";
import type { MessageEditing } from "./MessageEditing.ts";
import { Composer } from "../chat/Composer.tsx";
import { SessionIndicators } from "../chat/SessionIndicators.tsx";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { WorkingDirectoryPreferenceState } from "../preferences/useWorkingDirectoryPreferences.ts";
import { deriveConversationState } from "./ConversationState.ts";
import type { ConversationExecutionStatus } from "./ConversationState.ts";
import { Transcript } from "./Transcript.tsx";
import { SessionWorkingDirectory } from "./SessionWorkingDirectory.tsx";
import type { ReadingPosition } from "./Transcript.tsx";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../sessions/TranscriptNavigationStorage.ts";
import type { WorkspaceCommands, WorkspaceStopState } from "./WorkspaceCommands.ts";
import type { ChatState } from "./WorkspaceState.ts";
import { useSessionSnapshot } from "./useSessionSnapshot.ts";
import type { WorkspaceModelState } from "./useWorkspaceModels.ts";
import "./conversation.css";

const efforts = ["none", "low", "medium", "high", "extraHigh"];
const executionLabels: Record<Exclude<ConversationExecutionStatus, null>, string> = {
  stopping: "Stopping…",
  running: "Running…",
  accepted: "Accepted; awaiting session update.",
  compacting: "Compacting…",
};
const settlementLabels = { failed: "Execution failed.", cancelled: "Cancelled." };

interface ConversationProps {
  client: SessionClient;
  sessionId: string;
  active?: boolean;
  transcript?: TranscriptDisplay | undefined;
  streamId?: string | null;
  chat: ChatState;
  commands: WorkspaceCommands;
  messageEditing: MessageEditing;
  stop: WorkspaceStopState | undefined;
  checking: boolean;
  checkingContinuation: boolean;
  models: WorkspaceModelState;
  preferences: WorkingDirectoryPreferenceState;
  ready: boolean;
  canPersist: boolean;
  refresh: number;
  notice: string | null;
  onRefresh(): void;
  editDraft(id: string, text: string): boolean;
  selectModel(id: string, selection: LlmSelection): boolean;
  position: ReadingPosition | undefined;
  onPositionChange(position: ReadingPosition): void;
  transcriptPosition: TranscriptNavigationState | undefined;
  onTranscriptPositionChange(position: TranscriptNavigationState): void;
}

// Normal startup retains three recent chats plus focused or selected views.
export function Conversation({
  client,
  sessionId,
  active = true,
  transcript,
  streamId = null,
  chat,
  commands,
  messageEditing,
  stop,
  checking,
  checkingContinuation,
  models,
  preferences,
  ready,
  canPersist,
  refresh,
  notice,
  onRefresh,
  editDraft,
  selectModel,
  position,
  onPositionChange,
  transcriptPosition,
  onTranscriptPositionChange,
}: ConversationProps) {
  const observeSnapshot = useCallback(
    (snapshot: SessionSnapshot, knownWhenRead: string | null) =>
      commands.observeSnapshot(snapshot, knownWhenRead),
    [commands],
  );
  const {
    snapshot,
    history,
    error: readError,
    visible,
    acceptSnapshot,
  } = useSessionSnapshot(
    client,
    sessionId,
    refresh,
    chat.knownExecutionId,
    chat.pending?.status === "sending",
    observeSnapshot,
    notice,
    active,
    transcript === undefined,
  );
  const [workingDirectoryLocked, setWorkingDirectoryLocked] = useState(false);
  const [workingDirectoryNotice, setWorkingDirectoryNotice] = useState<string | null>(null);
  const workingDirectoryGate = useRef(false);
  const updateWorkingDirectoryGate = useCallback((locked: boolean, message: string | null) => {
    workingDirectoryGate.current = locked;
    setWorkingDirectoryLocked(locked);
    setWorkingDirectoryNotice(message);
  }, []);
  const pendingInputRemoval = commands.hasPendingInputRemoval(sessionId);
  const unresolvedInputRemovals = Object.values(commands.status.pendingInputRemovals).filter(
    (removal) => removal.sessionId === sessionId,
  );
  const state = deriveConversationState(
    chat,
    snapshot,
    stop,
    ready,
    canPersist,
    pendingInputRemoval,
  );
  const showWorkingDirectory = Boolean(
    snapshot?.canChangeWorkingDirectory === true &&
    snapshot.unsettledExecutionId === null &&
    !chat.pending &&
    !chat.pendingContinuation &&
    !chat.knownExecutionId &&
    !pendingInputRemoval,
  );
  const selection = state.selection;
  const modelKey = selection ? `${selection.connection.id}/${selection.modelId}` : null;
  const error = readError || chat.error || stop?.error;
  const editingContext = {
    controller: messageEditing,
    sessionId,
    draft: chat.messageEdit,
    selection,
    canEdit: Boolean(
      ready &&
      canPersist &&
      snapshot &&
      !snapshot.admissionFenced &&
      !chat.pending &&
      !chat.pendingContinuation &&
      !pendingInputRemoval,
    ),
  };
  function choose(next: LlmSelection) {
    if (!state.selectionLocked) selectModel(sessionId, next);
  }

  return (
    <section
      id={active ? "chat-view" : undefined}
      hidden={!active}
      data-chat-session={sessionId}
      class="chat-view"
      role="tabpanel"
      aria-labelledby={`tab-${sessionId}`}
    >
      <MessageEditingContext.Provider value={editingContext}>
        <Transcript
          client={client}
          display={transcript}
          streamId={streamId}
          history={history}
          historyRevision={snapshot?.historyRevision ?? null}
          notice={notice}
          sessionId={sessionId}
          visible={visible && notice !== null}
          position={position}
          onPositionChange={onPositionChange}
          transcriptPosition={transcriptPosition}
          onTranscriptPositionChange={onTranscriptPositionChange}
          emptyState={
            (showWorkingDirectory || workingDirectoryLocked) && snapshot ? (
              <SessionWorkingDirectory
                client={client}
                sessionId={sessionId}
                workingDirectory={snapshot.workingDirectory}
                preferences={preferences}
                disabled={!ready}
                onGateChange={updateWorkingDirectoryGate}
                onSnapshot={acceptSnapshot}
                onRefresh={onRefresh}
              />
            ) : null
          }
        >
          <div class="chat-status" aria-live="polite">
            {error && <span class="inline-error">{error} </span>}
            {workingDirectoryNotice && !showWorkingDirectory && (
              <span class={workingDirectoryLocked ? "inline-error" : undefined}>
                {workingDirectoryNotice}
              </span>
            )}
            {readError && <button onClick={onRefresh}>Refresh</button>}
            {state.admissionFenced && <span>Session admission is fenced. </span>}
            {state.unsettledWhileFenced && <span>Unsettled execution. </span>}
            {state.executionStatus && <span>{executionLabels[state.executionStatus]} </span>}
            {state.showStopRequested && <span>Stop requested. </span>}
            {state.settlementStatus && (
              <span class={state.settlementStatus === "failed" ? "inline-error" : undefined}>
                {state.settlementStatus === "failed"
                  ? snapshot?.lastSettlement?.error || settlementLabels.failed
                  : settlementLabels.cancelled}{" "}
              </span>
            )}
            {state.pendingStatus && (
              <span>
                {state.pendingStatus === "sending"
                  ? "Sending…"
                  : checking
                    ? "Checking receipt…"
                    : "Send uncertain."}{" "}
                {state.pendingStatus === "uncertain" && (
                  <>
                    <button
                      disabled={checking}
                      onClick={() => {
                        void commands.checkReceipt(sessionId);
                      }}
                    >
                      Check receipt
                    </button>
                    <button
                      disabled={!canPersist || checking}
                      onClick={() => {
                        void commands.retryOriginalSubmission(sessionId);
                      }}
                    >
                      Retry original
                    </button>
                  </>
                )}
              </span>
            )}
            {chat.pendingContinuation && (
              <span>
                {chat.pendingContinuation.status === "sending"
                  ? "Continuing… "
                  : checkingContinuation
                    ? "Checking Continue receipt… "
                    : "Continue uncertain. "}
                {chat.pendingContinuation.status === "uncertain" && (
                  <>
                    <button
                      disabled={checkingContinuation}
                      onClick={() => {
                        void commands.checkContinuationReceipt(sessionId);
                      }}
                    >
                      Check receipt
                    </button>
                    <button
                      disabled={!canPersist || checkingContinuation}
                      onClick={() => {
                        void commands.retryOriginalContinuation(sessionId);
                      }}
                    >
                      Retry original
                    </button>
                  </>
                )}
              </span>
            )}
            {chat.messageEdit?.submission && (
              <span>
                {chat.messageEdit.status === "saving" ? "Saving edit… " : "Save uncertain. "}
                {chat.messageEdit.status === "uncertain" && (
                  <>
                    {chat.messageEdit.error && (
                      <span class="inline-error">{chat.messageEdit.error} </span>
                    )}
                    <button
                      onClick={() => {
                        void messageEditing.reconcile(sessionId);
                      }}
                    >
                      Check receipt
                    </button>
                    <button
                      disabled={!canPersist}
                      onClick={() => {
                        void messageEditing.reconcile(sessionId, true);
                      }}
                    >
                      Retry save
                    </button>
                  </>
                )}
              </span>
            )}
            {state.textError && <span class="inline-error">{state.textError}</span>}
            {unresolvedInputRemovals.map((removal) => {
              const inputStillVisible = Boolean(
                snapshot?.unsettledExecutionId === removal.executionId &&
                snapshot.pendingInputs.some((input) => input.submissionId === removal.submissionId),
              );
              if (inputStillVisible) return null;
              return (
                <span class="chat-pending-input-reconcile" key={removal.submissionId}>
                  <span role="status">
                    {removal.status === "removing"
                      ? "Removing pending input…"
                      : removal.status === "checking"
                        ? "Checking pending-input removal…"
                        : "Pending-input removal status unknown."}
                  </span>
                  {removal.status === "uncertain" && (
                    <button
                      type="button"
                      aria-label={`Check removal status for input ${removal.submissionId}`}
                      onClick={() => {
                        void commands.checkPendingInputRemoval(
                          sessionId,
                          removal.executionId,
                          removal.submissionId,
                        );
                      }}
                    >
                      Check
                    </button>
                  )}
                </span>
              );
            })}
            {snapshot && snapshot.pendingInputs.length > 0 && (
              <details class="chat-pending-inputs">
                <summary>Pending ({snapshot.pendingInputs.length})</summary>
                <ol class="chat-pending-input-list">
                  {snapshot.pendingInputs.map((input) => {
                    const executionId = snapshot.unsettledExecutionId;
                    const removable = input.canRemove === true && executionId !== null;
                    const removal = executionId
                      ? commands.pendingInputRemoval(sessionId, executionId, input.submissionId)
                      : undefined;
                    return (
                      <li class="chat-pending-input" key={input.submissionId}>
                        <span class="chat-pending-input-preview">{input.preview}</span>
                        {removal?.status === "removing" ? (
                          <span role="status">Removing…</span>
                        ) : removal?.status === "checking" ? (
                          <span role="status">Checking removal…</span>
                        ) : removal?.status === "uncertain" ? (
                          <span class="chat-pending-input-check">
                            <span role="status">Removal status unknown.</span>
                            <button
                              type="button"
                              aria-label={`Check removal status for pending input: ${input.preview}`}
                              onClick={() => {
                                void commands.checkPendingInputRemoval(
                                  sessionId,
                                  executionId!,
                                  input.submissionId,
                                );
                              }}
                            >
                              Check
                            </button>
                          </span>
                        ) : removable ? (
                          <button
                            type="button"
                            class="chat-pending-input-remove"
                            aria-label={`Remove pending input: ${input.preview}`}
                            disabled={!ready}
                            onClick={() => {
                              void commands.removePendingInput(
                                sessionId,
                                executionId,
                                input.submissionId,
                                removable,
                              );
                            }}
                          >
                            Remove
                          </button>
                        ) : (
                          <span class="chat-pending-input-state">Sending…</span>
                        )}
                      </li>
                    );
                  })}
                </ol>
              </details>
            )}
          </div>
        </Transcript>
      </MessageEditingContext.Provider>
      {chat.messageEdit &&
        snapshot &&
        chat.messageEdit.target.historyRevision !== snapshot.historyRevision &&
        !chat.messageEdit.submission && (
          <InlineMessageEditor
            context={editingContext}
            draft={chat.messageEdit}
            onCancel={() => messageEditing.cancel(sessionId)}
          />
        )}
      <Composer
        indicators={
          <SessionIndicators
            contextUsage={snapshot?.contextUsage ?? null}
            contextWindow={selection?.contextWindow ?? null}
            workingTime={snapshot?.workingTime ?? null}
            active={active && visible && notice !== null}
            fenced={snapshot?.admissionFenced ?? true}
            sessionId={sessionId}
            usageActive={active && visible}
            usageBusy={state.active || chat.pending?.status === "sending"}
            usageRefresh={`${refresh}:${state.active || chat.pending?.status === "sending" ? "" : (notice ?? "")}`}
          />
        }
        text={chat.draft}
        onTextChange={(text) => {
          editDraft(sessionId, text);
        }}
        models={models.options}
        modelKey={modelKey}
        modelLabel={
          selection
            ? (models.options.find((model) => model.key === modelKey)?.modelLabel ??
              selection.modelId)
            : "Select model"
        }
        onModelChange={(key) => {
          const option = models.options.find((model) => model.key === key);
          if (option)
            choose({ ...option.selection, reasoningEffort: selection?.reasoningEffort ?? "high" });
        }}
        reasoningEffort={selection?.reasoningEffort ?? "high"}
        reasoningOptions={efforts}
        onReasoningChange={(effort) => {
          if (selection) choose({ ...selection, reasoningEffort: effort });
        }}
        selectionLocked={state.selectionLocked}
        canSend={state.canSend && !workingDirectoryLocked}
        canContinue={state.canContinue && !workingDirectoryLocked}
        continuationAvailable={state.continuationAvailable}
        working={state.active}
        stopping={state.executionStatus === "stopping"}
        onSend={() => {
          if (!workingDirectoryGate.current && snapshot)
            void commands.send(sessionId, snapshot, state.active, state.canSend);
        }}
        onContinue={() => {
          if (!workingDirectoryGate.current && snapshot)
            void commands.continueSession(sessionId, snapshot, state.canContinue);
        }}
        onStop={() => {
          if (snapshot && state.active) void commands.stop(sessionId, snapshot, state.stopId);
        }}
        onRefreshModels={() => {
          void models.refresh();
        }}
        refreshingModels={models.refreshing}
        modelsError={models.error}
      />
    </section>
  );
}
