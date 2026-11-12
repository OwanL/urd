import { useCallback } from "preact/hooks";
import { MessageEditingContext, InlineMessageEditor } from "./EditableMessage.tsx";
import type { MessageEditing } from "./MessageEditing.ts";
import { Composer } from "../chat/Composer.tsx";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { deriveConversationState } from "./ConversationState.ts";
import type { ConversationExecutionStatus } from "./ConversationState.ts";
import { Transcript } from "./Transcript.tsx";
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
  models: WorkspaceModelState;
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
  models,
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
  const state = deriveConversationState(chat, snapshot, stop, ready, canPersist);
  const selection = state.selection;
  const modelKey = selection ? `${selection.connection.id}/${selection.modelId}` : null;
  const error = readError || chat.error || stop?.error;
  const editingContext = {
    controller: messageEditing,
    sessionId,
    draft: chat.messageEdit,
    selection,
    canEdit: Boolean(ready && canPersist && snapshot && !snapshot.admissionFenced && !chat.pending),
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
        >
          <div class="chat-status" aria-live="polite">
            {error && <span class="inline-error">{error} </span>}
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
            {snapshot && snapshot.pendingInputs.length > 0 && (
              <details>
                <summary>Pending ({snapshot.pendingInputs.length})</summary>
                <ol>
                  {snapshot.pendingInputs.map((input) => (
                    <li key={input.submissionId}>{input.preview}</li>
                  ))}
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
            choose({ ...option.selection, reasoningEffort: selection?.reasoningEffort ?? null });
        }}
        reasoningEffort={selection?.reasoningEffort ?? null}
        reasoningOptions={efforts}
        onReasoningChange={(effort) => {
          if (selection) choose({ ...selection, reasoningEffort: effort });
        }}
        selectionLocked={state.selectionLocked}
        canSend={state.canSend}
        working={state.active}
        stopping={state.executionStatus === "stopping"}
        onSend={() => {
          if (snapshot) void commands.send(sessionId, snapshot, state.active, state.canSend);
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
