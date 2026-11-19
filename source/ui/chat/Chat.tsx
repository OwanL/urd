import { useCallback, useMemo, useRef, useState } from "preact/hooks";
import { MessageEditingContext, InlineMessageEditor } from "./message-editing/EditableMessage.tsx";
import type { MessageEditing } from "./message-editing/MessageEditing.ts";
import { Composer } from "./composer/Composer.tsx";
import { SubagentProviderControl } from "./composer/SubagentProviderControl.tsx";
import { SessionIndicators } from "./composer/SessionIndicators.tsx";
import { UsageOverview } from "../usage/UsageOverview.tsx";
import { sameContextModel, type LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { ModelBucketSetup } from "./ModelBucketSetup.tsx";
import { preferenceSaveBlocks } from "../preferences/usePreferences.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { PendingInputQueue } from "./PendingInputQueue.tsx";
import { deriveChatStatus } from "./ChatStatus.ts";
import type { ChatExecutionStatus } from "./ChatStatus.ts";
import { Transcript } from "./transcript/Transcript.tsx";
import { SessionWorkingDirectory } from "./working-directory/SessionWorkingDirectory.tsx";
import { PendingResponseStatus } from "./prepass/PrepassRows.tsx";
import type { ResponseStatusContext } from "./prepass/PrepassRows.tsx";
import { unanchoredPrepass, uniqueModelNames } from "./prepass/PrepassPresentation.ts";
import type { ReadingPosition } from "./transcript/Transcript.tsx";
import type { TranscriptDisplay } from "./transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "./transcript/navigation/TranscriptNavigationStorage.ts";
import type { ChatCommands, ChatStopState } from "./ChatCommands.ts";
import type { ChatState } from "./ChatState.ts";
import { useSessionSnapshot } from "../sessions/useSessionSnapshot.ts";
import type { ModelOptionsState } from "../providers/models/useModelOptions.ts";
import "./chat.css";

const efforts = ["none", "low", "medium", "high", "extraHigh"];
const executionLabels: Record<Exclude<ChatExecutionStatus, null>, string> = {
  stopping: "Stopping…",
  running: "Running…",
  accepted: "Accepted; awaiting session update.",
  compacting: "Compacting…",
};
const settlementLabels = { failed: "Execution failed.", cancelled: "Cancelled." };

interface ChatProps {
  client: SessionClient;
  providerClient: ProviderClient;
  sessionId: string;
  active?: boolean;
  transcript?: TranscriptDisplay | undefined;
  streamId?: string | null;
  chat: ChatState;
  commands: ChatCommands;
  messageEditing: MessageEditing;
  stop: ChatStopState | undefined;
  checking: boolean;
  checkingContinuation: boolean;
  models: ModelOptionsState;
  preferences: PreferencesState;
  connected: boolean;
  unavailable: boolean;
  sessionStatus: string | undefined;
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
export function Chat({
  client,
  providerClient,
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
  sessionStatus,
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
}: ChatProps) {
  const ready = connected && !unavailable;
  const savingSubagentProviders = commands.status.savingSubagentProviders.has(sessionId);
  const executionSettingsSaving =
    savingSubagentProviders || preferenceSaveBlocks(preferences.pendingFor, "execute");
  const subagentProviderError = commands.status.subagentProviderError;
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
  const composerInput = useRef<HTMLTextAreaElement>(null);
  const updateWorkingDirectoryGate = useCallback((locked: boolean, message: string | null) => {
    workingDirectoryGate.current = locked;
    setWorkingDirectoryLocked(locked);
    setWorkingDirectoryNotice(message);
  }, []);
  const pendingInputRemoval = commands.hasPendingInputRemoval(sessionId);
  const compaction = commands.status.compactions[sessionId];
  const compactionPending = compaction?.phase === "starting" || compaction?.phase === "observing";
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
  const state = deriveChatStatus(
    chat,
    snapshot,
    stop,
    ready,
    canPersist,
    pendingInputRemoval || compactionPending,
  );
  const canCompact = Boolean(
    active &&
    visible &&
    ready &&
    notice !== null &&
    !readError &&
    (sessionStatus === "idle" || sessionStatus === "failed") &&
    snapshot?.canCompact === true &&
    compaction?.phase !== "uncertain" &&
    canPersist &&
    state.selection &&
    !state.selectionLocked &&
    !stop &&
    !workingDirectoryLocked &&
    !executionSettingsSaving,
  );
  const showWorkingDirectory = Boolean(
    snapshot?.canChangeWorkingDirectory === true &&
    snapshot.unsettledExecutionId === null &&
    !chat.pending &&
    !chat.pendingContinuation &&
    !chat.knownExecutionId &&
    !pendingInputRemoval &&
    !compactionPending,
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
      !pendingInputRemoval &&
      !compactionPending,
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
          renderPrepass={
            snapshot?.pendingInputs.some((input) => input.isClaimed) || pendingResponse
              ? (entries) => (
                  <>
                    <PendingInputQueue
                      placement="preparing"
                      client={client}
                      sessionId={sessionId}
                      snapshot={snapshot}
                      commands={commands}
                      ready={ready}
                    />
                    {snapshot && pendingResponse && (
                      <PendingResponseStatus
                        header={pendingResponse.header}
                        preparation={unanchoredPrepass(
                          pendingResponse.preparation,
                          entries,
                          snapshot.historyRevision,
                        )}
                        context={statusContext}
                        historyRevision={snapshot.historyRevision}
                      />
                    )}
                  </>
                )
              : undefined
          }
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
                {showWorkingDirectory && models.hasProviders && (
                  <ModelBucketSetup
                    preferences={preferences}
                    models={models}
                    disabled={!ready}
                    onCompleteFocus={() => {
                      if (composerInput.current?.getClientRects().length)
                        composerInput.current.focus({ preventScroll: true });
                    }}
                  />
                )}
              </div>
            ) : null
          }
        >
          <PendingInputQueue
            placement="queued"
            client={client}
            sessionId={sessionId}
            snapshot={snapshot}
            commands={commands}
            ready={ready}
          />
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
                      disabled={!canPersist || checking || executionSettingsSaving}
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
                      disabled={!canPersist || checkingContinuation || executionSettingsSaving}
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
        input={composerInput}
        subagentProviders={
          active && (
            <SubagentProviderControl
              sessionId={sessionId}
              sessionClient={client}
              providerClient={providerClient}
              ready={ready}
              saving={savingSubagentProviders}
              saveError={
                subagentProviderError?.sessionId === sessionId
                  ? subagentProviderError.message
                  : null
              }
              onSave={(ids) => commands.saveSubagentProviders(sessionId, ids)}
            />
          )
        }
        indicators={
          <SessionIndicators
            contextUsage={
              sameContextModel(selection, snapshot?.latestLlmSelection ?? null)
                ? (snapshot?.contextUsage ?? null)
                : null
            }
            contextWindow={selection?.contextWindow ?? null}
            canCompact={canCompact}
            compacting={compactionPending || (state.active && snapshot?.isCompacting === true)}
            compactionMessage={compaction && "message" in compaction ? compaction.message : null}
            onCheckCompaction={compaction?.phase === "uncertain" && ready ? onRefresh : undefined}
            onCompact={() => {
              if (!workingDirectoryGate.current && snapshot)
                void commands.compact(sessionId, snapshot, canCompact);
            }}
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
        configurationPending={executionSettingsSaving}
        canSend={state.canSend && !workingDirectoryLocked && !executionSettingsSaving}
        canContinue={state.canContinue && !workingDirectoryLocked && !executionSettingsSaving}
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
