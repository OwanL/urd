import { useCallback, useMemo, useRef, useState } from "preact/hooks";
import { MessageEditingContext, InlineMessageEditor } from "./message-editing/EditableMessage.tsx";
import type { MessageEditing } from "./message-editing/MessageEditing.ts";
import { Composer } from "./composer/Composer.tsx";
import { SubagentProviderControl } from "./composer/SubagentProviderControl.tsx";
import { SessionIndicators } from "./composer/SessionIndicators.tsx";
import { sameContextModel, type LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { preferenceSaveBlocks } from "../preferences/usePreferences.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { PendingInputQueue } from "./PendingInputQueue.tsx";
import { deriveChatStatus } from "./ChatStatus.ts";
import { ChatStatusDisplay } from "./ChatStatusDisplay.tsx";
import { EmptyChatOverview } from "./EmptyChatOverview.tsx";
import { Transcript } from "./transcript/Transcript.tsx";
import { PendingResponseStatus } from "./prepass/PrepassRows.tsx";
import type { ResponseStatusContext } from "./prepass/PrepassRows.tsx";
import { unanchoredPrepass, uniqueModelNames } from "./prepass/PrepassPresentation.ts";
import type { ReadingPosition } from "./transcript/Transcript.tsx";
import type { TranscriptDisplay } from "./transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "./transcript/navigation/TranscriptNavigationStorage.ts";
import { compactionStatus } from "./ChatCommands.ts";
import type { ChatCommands, ChatStopState } from "./ChatCommands.ts";
import type { ChatState } from "./ChatState.ts";
import { useSessionSnapshot } from "../sessions/useSessionSnapshot.ts";
import type { ModelOptionsState } from "../providers/models/useModelOptions.ts";
import { UserQuestions } from "./ask-user/UserQuestions.tsx";
import { BrowserSignInPrompt } from "./playwright/BrowserSignInPrompt.tsx";
import "./chat.css";

const efforts = ["none", "low", "medium", "high", "extraHigh"];

interface ChatProps {
  client: SessionClient;
  providerClient: ProviderClient;
  sessionId: string;
  active?: boolean;
  transcript?: TranscriptDisplay | undefined;
  streamId?: string | null;
  chat: ChatState;
  defaultSelection: LlmSelection | null;
  commands: ChatCommands;
  messageEditing: MessageEditing;
  stop: ChatStopState | undefined;
  checking: boolean;
  checkingContinuation: boolean;
  models: ModelOptionsState;
  preferences: PreferencesState;
  connected: boolean;
  unavailable: boolean;
  closing: boolean;
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
  defaultSelection,
  commands,
  messageEditing,
  stop,
  checking,
  checkingContinuation,
  models,
  preferences,
  connected,
  unavailable,
  closing,
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
  const ready = connected && !unavailable && !closing;
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
  const compactionPending =
    compaction?.phase === "starting" || (compaction?.phase === "observing" && !compaction.queued);
  const currentCompactionStatus = compactionStatus(snapshot, compaction);
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
    defaultSelection,
  );
  const canCompact = Boolean(
    active &&
    visible &&
    ready &&
    notice !== null &&
    !readError &&
    snapshot?.canCompact === true &&
    (!compaction || compaction.phase === "settled") &&
    canPersist &&
    state.selection &&
    !chat.pending &&
    !chat.pendingContinuation &&
    !chat.messageEdit &&
    !pendingInputRemoval &&
    !snapshot.admissionFenced &&
    !snapshot.stopRequested &&
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
              <EmptyChatOverview
                client={client}
                sessionId={sessionId}
                snapshot={snapshot}
                preferences={preferences}
                ready={ready}
                onGateChange={updateWorkingDirectoryGate}
                onSnapshot={acceptSnapshot}
                onRefresh={onRefresh}
                active={active}
                visible={visible}
                refresh={refresh}
              />
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
          <ChatStatusDisplay
            error={error}
            workingDirectoryNotice={workingDirectoryNotice}
            showWorkingDirectory={showWorkingDirectory}
            workingDirectoryLocked={workingDirectoryLocked}
            readError={readError}
            onRefresh={onRefresh}
            provisioning={provisioning}
            state={state}
            settlementError={snapshot?.lastSettlement?.error}
            continuationStatus={chat.pendingContinuation?.status ?? null}
            editStatus={
              chat.messageEdit?.submission
                ? { status: chat.messageEdit.status, error: chat.messageEdit.error }
                : null
            }
            checking={checking}
            checkingContinuation={checkingContinuation}
            canPersist={canPersist}
            executionSettingsSaving={executionSettingsSaving}
            onCheckReceipt={() => void commands.checkReceipt(sessionId)}
            onRetrySubmission={() => void commands.retryOriginalSubmission(sessionId)}
            onCheckContinuation={() => void commands.checkContinuationReceipt(sessionId)}
            onRetryContinuation={() => void commands.retryOriginalContinuation(sessionId)}
            onCheckEdit={() => void messageEditing.reconcile(sessionId)}
            onRetryEdit={() => void messageEditing.reconcile(sessionId, true)}
          />
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
      {active && (
        <>
          <UserQuestions client={client} sessionId={sessionId} connected={ready} notice={notice} />
          <BrowserSignInPrompt key={sessionId} client={client} sessionId={sessionId} connected={ready} notice={notice} />
        </>
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
            compactionStatus={currentCompactionStatus}
            compactionMessage={compaction && "message" in compaction ? compaction.message : null}
            compactionUnavailableReason={snapshot?.compactionUnavailableReason ?? null}
            onCheckCompaction={compaction?.phase === "uncertain" && ready ? onRefresh : undefined}
            onCancelCompaction={
              ready && snapshot && !(compaction?.phase === "observing" && compaction.cancelling)
                ? () => void commands.cancelCompaction(sessionId, snapshot)
                : undefined
            }
            onCompact={() => {
              if (!workingDirectoryGate.current && snapshot)
                void commands.compact(sessionId, snapshot, canCompact, defaultSelection);
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
            void commands.send(sessionId, snapshot, state.active, state.canSend, defaultSelection);
        }}
        onContinue={() => {
          if (!workingDirectoryGate.current && snapshot)
            void commands.continueSession(sessionId, snapshot, state.canContinue, defaultSelection);
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
