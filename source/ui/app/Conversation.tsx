import { useCallback } from "preact/hooks";
import { Composer } from "../chat/Composer.tsx";
import type { LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { deriveConversationState } from "./ConversationState.ts";
import { Transcript } from "./Transcript.tsx";
import type { ReadingPosition } from "./Transcript.tsx";
import type { WorkspaceCommands, WorkspaceStopState } from "./WorkspaceCommands.ts";
import type { ChatState } from "./WorkspaceState.ts";
import { useSessionSnapshot } from "./useSessionSnapshot.ts";
import type { WorkspaceModelState } from "./useWorkspaceModels.ts";
import "./conversation.css";

const efforts = ["none", "low", "medium", "high", "extraHigh"];
const executionLabels = {
  stopping: "Stopping…",
  running: "Running…",
  accepted: "Accepted; awaiting session update.",
};
const settlementLabels = { failed: "Execution failed.", cancelled: "Cancelled." };

interface ConversationProps {
  client: SessionClient;
  sessionId: string;
  chat: ChatState;
  commands: WorkspaceCommands;
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
}

// Mounted for the active chat only; commands and retained reading positions outlive this view.
export function Conversation({
  client,
  sessionId,
  chat,
  commands,
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
  );
  const state = deriveConversationState(chat, snapshot, stop, ready, canPersist);
  const selection = state.selection;
  const modelKey = selection ? `${selection.connection.id}/${selection.modelId}` : null;
  const error = readError || chat.error || stop?.error;
  function choose(next: LlmSelection) {
    if (!state.selectionLocked) selectModel(sessionId, next);
  }

  return (
    <section id="chat-view" class="chat-view" role="tabpanel" aria-labelledby={`tab-${sessionId}`}>
      <Transcript
        client={client}
        history={history}
        sessionId={sessionId}
        visible={visible && notice !== null}
        position={position}
        onPositionChange={onPositionChange}
      />
      <div class="chat-status" aria-live="polite">
        {error && <span class="inline-error">{error} </span>}
        {readError && <button onClick={onRefresh}>Refresh</button>}
        {state.admissionFenced && <span>Session admission is fenced. </span>}
        {state.unsettledWhileFenced && <span>Unsettled execution. </span>}
        {state.executionStatus && <span>{executionLabels[state.executionStatus]} </span>}
        {state.showStopRequested && <span>Stop requested. </span>}
        {state.settlementStatus && (
          <span class={state.settlementStatus === "failed" ? "inline-error" : undefined}>
            {settlementLabels[state.settlementStatus]}{" "}
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
                    void commands.reconcile(sessionId, false);
                  }}
                >
                  Check receipt
                </button>
                <button
                  disabled={!canPersist || checking}
                  onClick={() => {
                    void commands.reconcile(sessionId, true);
                  }}
                >
                  Retry original
                </button>
              </>
            )}
          </span>
        )}
        {state.textError && <span class="inline-error">{state.textError}</span>}
      </div>
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
          if (snapshot) void commands.stop(sessionId, snapshot, state.stopId);
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
