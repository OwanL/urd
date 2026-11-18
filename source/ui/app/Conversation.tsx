import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import { MessageEditingContext, InlineMessageEditor } from "./EditableMessage.tsx";
import type { MessageEditing } from "./MessageEditing.ts";
import { Composer } from "../chat/Composer.tsx";
import { SessionIndicators } from "../chat/SessionIndicators.tsx";
import { UsageOverview } from "../usage/UsageOverview.tsx";
import { IconButton } from "../components/IconButton.tsx";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import { deriveConversationState } from "./ConversationState.ts";
import type { ConversationExecutionStatus } from "./ConversationState.ts";
import { Transcript } from "./Transcript.tsx";
import { SessionWorkingDirectory } from "./SessionWorkingDirectory.tsx";
import { PendingResponseStatus } from "./PreparationRows.tsx";
import type { ResponseStatusContext } from "./PreparationRows.tsx";
import { uniqueModelNames } from "./PrepassValues.ts";
import type { ReadingPosition } from "./Transcript.tsx";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../sessions/TranscriptNavigationStorage.ts";
import type {
  PendingInputRemovalState,
  WorkspaceCommands,
  WorkspaceStopState,
} from "./WorkspaceCommands.ts";
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

function PendingInputBubble({
  preview,
  removalStatus,
  removeLabel,
  checkLabel,
  disabled = false,
  onRemove,
  onCheck,
}: {
  preview: string | null;
  removalStatus: PendingInputRemovalState["status"] | undefined;
  removeLabel?: string | undefined;
  checkLabel?: string | undefined;
  disabled?: boolean;
  onRemove?: (() => void) | undefined;
  onCheck?: (() => void) | undefined;
}) {
  const previewElement = useRef<HTMLDivElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [canExpand, setCanExpand] = useState(false);

  useLayoutEffect(() => {
    const node = previewElement.current;
    if (!node || preview === null || expanded) return;

    const updateOverflow = () => setCanExpand(node.scrollHeight > node.clientHeight + 1);
    updateOverflow();
    const observer = new ResizeObserver(updateOverflow);
    observer.observe(node);
    return () => observer.disconnect();
  }, [expanded, preview]);

  const status =
    removalStatus === "removing"
      ? "Removing…"
      : removalStatus === "checking"
        ? "Checking removal…"
        : removalStatus === "uncertain"
          ? "Removal status unknown."
          : null;

  return (
    <div class={`chat-pending-input${removalStatus ? " has-removal-state" : ""}`}>
      <div
        ref={previewElement}
        role={preview === null ? "status" : undefined}
        class={`chat-pending-input-preview message-text${expanded ? " is-expanded" : ""}`}
      >
        {preview ?? status}
      </div>
      <div class="chat-pending-input-actions">
        {canExpand && (
          <IconButton
            icon="chevron"
            label={`${expanded ? "Collapse" : "Expand"} queued input preview`}
            hint={`${expanded ? "Collapse" : "Expand"} preview`}
            class={`chat-pending-input-expand${expanded ? " is-expanded" : ""}`}
            aria-expanded={expanded}
            onClick={() => setExpanded((value) => !value)}
          />
        )}
        {removalStatus === "uncertain" && onCheck && (
          <button
            type="button"
            class="chat-pending-input-check"
            aria-label={checkLabel}
            onClick={onCheck}
          >
            Check
          </button>
        )}
        {!removalStatus && removeLabel && onRemove && (
          <IconButton
            icon="close"
            label={removeLabel}
            hint="Remove queued message"
            class="chat-pending-input-remove"
            disabled={disabled}
            onClick={onRemove}
          />
        )}
      </div>
      {preview !== null && (status || (!removalStatus && !removeLabel)) && (
        <span class="chat-pending-input-status" role="status">
          {status ?? "Sending…"}
        </span>
      )}
    </div>
  );
}

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
  preferences: PreferencesState;
  connected: boolean;
  unavailable: boolean;
  canPersist: boolean;
  refresh: number;
  notice: string | null;
  provisioning: string | null;
  onRefresh(): void;
  onSetUpProvider(): void;
  onChooseModels(): void;
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
  connected,
  unavailable,
  canPersist,
  refresh,
  notice,
  provisioning,
  onRefresh,
  onSetUpProvider,
  onChooseModels,
  editDraft,
  selectModel,
  position,
  onPositionChange,
  transcriptPosition,
  onTranscriptPositionChange,
}: ConversationProps) {
  const ready = connected && !unavailable;
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
    active && !unavailable,
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
  const pendingInputs = snapshot?.pendingInputs ?? [];
  const pendingExecutionId = snapshot?.unsettledExecutionId ?? null;
  const statusContext: ResponseStatusContext = useMemo(
    () => ({
      client,
      sessionId,
      modelNames: uniqueModelNames(
        models.options.map((option) => ({
          modelId: option.selection.modelId,
          modelLabel: option.modelLabel,
        })),
      ),
      visible: visible && notice !== null,
      onChooseModels,
    }),
    [client, sessionId, models.options, visible, notice, onChooseModels],
  );
  const pendingResponse = snapshot?.pendingResponse ?? null;
  const unlistedInputRemovals = unresolvedInputRemovals.filter(
    (removal) =>
      pendingExecutionId !== removal.executionId ||
      !pendingInputs.some((input) => input.submissionId === removal.submissionId),
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
          onChooseModels={onChooseModels}
          emptyState={
            unavailable ? (
              <p>Session unavailable on this server.</p>
            ) : (showWorkingDirectory || workingDirectoryLocked) && snapshot ? (
              <div class="empty-chat-overview">
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
                <UsageOverview active={active && visible} refreshKey={refresh} />
              </div>
            ) : null
          }
        >
          {(pendingInputs.length > 0 || unlistedInputRemovals.length > 0 || pendingResponse) && (
            <>
              {(pendingInputs.length > 0 || unlistedInputRemovals.length > 0) && (
                <section class="chat-pending-inputs" aria-label="Queued inputs">
                  <div class="chat-pending-inputs-label">
                    {pendingInputs.length ? `Queued · ${pendingInputs.length}` : "Queue update"}
                  </div>
                  {pendingInputs.map((input) => {
                    const executionId = pendingExecutionId;
                    const removable = input.canRemove === true && executionId !== null;
                    const removal = executionId
                      ? commands.pendingInputRemoval(sessionId, executionId, input.submissionId)
                      : undefined;
                    return (
                      <PendingInputBubble
                        key={input.submissionId}
                        preview={input.preview}
                        removalStatus={removal?.status}
                        removeLabel={
                          removable ? `Remove queued input: ${input.preview}` : undefined
                        }
                        checkLabel={`Check removal status for pending input: ${input.preview}`}
                        disabled={!ready}
                        onRemove={
                          removable
                            ? () => {
                                void commands.removePendingInput(
                                  sessionId,
                                  executionId!,
                                  input.submissionId,
                                  removable,
                                );
                              }
                            : undefined
                        }
                        onCheck={
                          removal?.status === "uncertain" && executionId
                            ? () => {
                                void commands.checkPendingInputRemoval(
                                  sessionId,
                                  executionId,
                                  input.submissionId,
                                );
                              }
                            : undefined
                        }
                      />
                    );
                  })}
                  {unlistedInputRemovals.map((removal) => {
                    return (
                      <PendingInputBubble
                        key={removal.submissionId}
                        preview={null}
                        removalStatus={removal.status}
                        checkLabel={`Check removal status for input ${removal.submissionId}`}
                        onCheck={
                          removal.status === "uncertain"
                            ? () => {
                                void commands.checkPendingInputRemoval(
                                  sessionId,
                                  removal.executionId,
                                  removal.submissionId,
                                );
                              }
                            : undefined
                        }
                      />
                    );
                  })}
                </section>
              )}
              {pendingResponse && (
                <PendingResponseStatus
                  header={pendingResponse.header}
                  preparation={pendingResponse.preparation}
                  context={statusContext}
                  historyRevision={snapshot?.historyRevision ?? 0}
                />
              )}
            </>
          )}
          <div class="chat-status" aria-live="polite">
            {error && <span class="inline-error">{error} </span>}
            {workingDirectoryNotice && !showWorkingDirectory && (
              <span class={workingDirectoryLocked ? "inline-error" : undefined}>
                {workingDirectoryNotice}
              </span>
            )}
            {readError && <button onClick={onRefresh}>Refresh</button>}
            {provisioning && <span>{provisioning} </span>}
            {state.admissionFenced && <span>Session admission is fenced. </span>}
            {state.unsettledWhileFenced && <span>Unsettled execution. </span>}
            {state.executionStatus && (
              <span
                class={
                  state.executionStatus === "running" ? "chat-running-announcement" : undefined
                }
              >
                {executionLabels[state.executionStatus]}{" "}
              </span>
            )}
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
        hasProviders={models.hasProviders}
        loadingModels={models.loading}
        refreshingModels={models.refreshing}
        modelsError={models.error}
        onRetryModels={() => {
          void models.retry();
        }}
        onSetUpProvider={onSetUpProvider}
      />
    </section>
  );
}
