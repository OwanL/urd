import type { ComponentChildren } from "preact";
import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import { MessageEditingContext, InlineMessageEditor } from "./message-editing/EditableMessage.tsx";
import type { MessageEditing } from "./message-editing/MessageEditing.ts";
import { Composer } from "./composer/Composer.tsx";
import { SubagentProviderControl } from "./composer/SubagentProviderControl.tsx";
import { SessionIndicators } from "./composer/SessionIndicators.tsx";
import { sameContextModel, type LlmSelection } from "../providers/LlmSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { ToolDraftStore } from "../sessions/ToolDrafts.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import { preferenceSaveBlocks } from "../preferences/usePreferences.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { PendingInputQueue } from "./PendingInputQueue.tsx";
import { deriveChatStatus, rateLimitRetryTime } from "./ChatStatus.ts";
import { ChatStatusDisplay } from "./ChatStatusDisplay.tsx";
import { EmptyChatOverview } from "./EmptyChatOverview.tsx";
import { Transcript } from "./transcript/Transcript.tsx";
import { PendingResponseStatus } from "./prepass/PrepassRows.tsx";
import type { ResponseStatusContext } from "./prepass/PrepassRows.tsx";
import { unanchoredPrepass, uniqueModelNames } from "./prepass/PrepassPresentation.ts";
import type { ReadingPosition } from "./transcript/Transcript.tsx";
import type { TranscriptDisplay } from "./transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "./transcript/navigation/TranscriptNavigationStorage.ts";
import { compactionBlocksSend, compactionStatus } from "./ChatCommands.ts";
import type { ChatCommands, ChatStopState } from "./ChatCommands.ts";
import type { ChatState } from "./ChatState.ts";
import { useSessionSnapshot } from "../sessions/useSessionSnapshot.ts";
import type { ModelOptionsState } from "../providers/models/useModelOptions.ts";
import type { UserImage } from "../sessions/transcript/Messages.ts";
import { DraftImagePreviews } from "./user-images/DraftImagePreviews.tsx";
import {
  imageLimitError,
  imageModelError,
  maxUserImages,
  pasteTarget,
  pastedImages,
} from "./user-images/ImagePaste.ts";
import { useImageUploads } from "./user-images/useImageUploads.ts";
import { UserQuestions } from "./ask-user/UserQuestions.tsx";
import { BrowserViewer } from "./browser/BrowserViewer.tsx";
import "./chat.css";

const efforts = ["none", "low", "medium", "high", "extraHigh"];

interface ChatProps {
  client: SessionClient;
  providerClient: ProviderClient;
  sessionId: string;
  active?: boolean;
  pasteEnabled: boolean;
  onUploadsPendingChange(id: string, pending: boolean): void;
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
  browserRefresh: number;
  notice: string | null;
  draftStore?: ToolDraftStore | undefined;
  provisioning: string | null;
  onRefresh(): void;
  onSetUpProvider(): void;
  onChooseModels(): void;
  editDraft(id: string, text: string): boolean;
  appendDraftImage(id: string, image: UserImage): boolean;
  removeDraftImage(id: string, imageId: string): boolean;
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
  pasteEnabled,
  onUploadsPendingChange,
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
  browserRefresh,
  notice,
  draftStore,
  provisioning,
  onRefresh,
  onSetUpProvider,
  onChooseModels,
  editDraft,
  appendDraftImage,
  removeDraftImage,
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
  const [browserRequest, setBrowserRequest] = useState<{
    ownerId: string;
    sequence: number;
  } | null>(null);
  const [browserOwners, setBrowserOwners] = useState<readonly string[]>([]);
  const [workingDirectoryLocked, setWorkingDirectoryLocked] = useState(false);
  const [workingDirectoryNotice, setWorkingDirectoryNotice] = useState<string | null>(null);
  const workingDirectoryGate = useRef(false);
  const composerInput = useRef<HTMLTextAreaElement>(null);
  const questionPanel = useRef<HTMLElement>(null);
  const [hasQuestion, setHasQuestion] = useState(false);
  const panel = useRef<HTMLElement>(null);
  const editTarget = chat.messageEdit?.target ?? null;
  const composerUploads = useImageUploads(sessionId, sessionId, chat.draftImages, (image) =>
    appendDraftImage(sessionId, image),
  );
  const editUploads = useImageUploads(
    sessionId,
    editTarget,
    chat.messageEdit?.images ?? [],
    (image, target) => (target ? messageEditing.appendImage(sessionId, target, image) : false),
  );
  const uploadsPending = composerUploads.images.length > 0 || editUploads.images.length > 0;
  useLayoutEffect(() => {
    if (!uploadsPending) return;
    onUploadsPendingChange(sessionId, true);
    return () => onUploadsPendingChange(sessionId, false);
  }, [sessionId, uploadsPending, onUploadsPendingChange]);
  const readPaste = useRef<(event: ClipboardEvent) => void>(() => {});
  readPaste.current = (event) => {
    if (event.defaultPrevented || !canPersist || closing || unavailable || panel.current?.hidden)
      return;
    const input = composerInput.current;
    const target = pasteTarget(event.composedPath(), input);
    if (target === "other") return;
    if (
      target instanceof HTMLTextAreaElement &&
      (!panel.current?.contains(target) || chat.messageEdit?.status !== "editing")
    )
      return;
    const images = pastedImages(event.clipboardData);
    if (!images.length) return;
    event.preventDefault();
    if (target instanceof HTMLTextAreaElement) {
      editUploads.paste(images, chat.messageEdit?.images ?? []);
    } else {
      if (document.activeElement !== input) input?.focus({ preventScroll: true });
      composerUploads.paste(images, chat.draftImages);
    }
  };
  useLayoutEffect(() => {
    if (!active || !pasteEnabled) return;
    const paste = (event: ClipboardEvent) => readPaste.current(event);
    window.addEventListener("paste", paste);
    return () => window.removeEventListener("paste", paste);
  }, [active, pasteEnabled]);
  const updateWorkingDirectoryGate = useCallback((locked: boolean, message: string | null) => {
    workingDirectoryGate.current = locked;
    setWorkingDirectoryLocked(locked);
    setWorkingDirectoryNotice(message);
  }, []);
  // Workspace passes handler props as fresh closures on every render, and they reach
  // response-status contexts inside the transcript; routing through the latest ref
  // keeps those context objects reference-stable across draft-only updates.
  const chooseModelsRef = useRef(onChooseModels);
  chooseModelsRef.current = onChooseModels;
  const chooseModels = useCallback(() => chooseModelsRef.current(), []);
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
      onChooseModels: chooseModels,
    }),
    [client, sessionId, models.options, visible, notice, chooseModels],
  );
  const pendingResponse = snapshot?.pendingResponse ?? null;
  const state = deriveChatStatus(
    chat,
    snapshot,
    stop,
    ready,
    canPersist,
    pendingInputRemoval || compactionBlocksSend(snapshot, compaction),
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
  const hasComposerImages = chat.draftImages.length > 0 || composerUploads.images.length > 0;
  const imageBlocked = hasComposerImages && selection?.supportsImages === false;
  const tooManyImages = chat.draftImages.length > maxUserImages;
  const sendingBlocked = imageBlocked || tooManyImages || composerUploads.images.length > 0;
  const modelKey = selection ? `${selection.connection.id}/${selection.modelId}` : null;
  const error = readError || chat.error || stop?.error;
  const canEdit = Boolean(
    ready &&
    canPersist &&
    snapshot &&
    !snapshot.admissionFenced &&
    !chat.pending &&
    !chat.pendingContinuation &&
    !pendingInputRemoval &&
    !compactionPending,
  );
  const editingContext = useMemo(
    () => ({
      controller: messageEditing,
      sessionId,
      draft: chat.messageEdit,
      uploads: editUploads,
      active,
      selection,
      canEdit,
    }),
    [messageEditing, sessionId, chat.messageEdit, editUploads, active, selection, canEdit],
  );
  function choose(next: LlmSelection) {
    if (!state.selectionLocked) selectModel(sessionId, next);
  }

  const renderChat = (browserLauncher: ComponentChildren) => (
    <>
      <MessageEditingContext.Provider value={editingContext}>
        <Transcript
          client={client}
          display={transcript}
          streamId={streamId}
          history={history}
          historyRevision={snapshot?.historyRevision ?? null}
          notice={notice}
          draftStore={draftStore}
          sessionId={sessionId}
          visible={visible && notice !== null}
          position={position}
          onPositionChange={onPositionChange}
          transcriptPosition={transcriptPosition}
          onTranscriptPositionChange={onTranscriptPositionChange}
          onChooseModels={chooseModels}
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
            rateLimitRetryTime={
              active && visible && notice !== null
                ? rateLimitRetryTime(state.executionStatus, snapshot?.rateLimitRetryAt)
                : null
            }
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
        <UserQuestions
          client={client}
          sessionId={sessionId}
          connected={ready}
          notice={notice}
          browserOwners={browserOwners}
          panelRef={questionPanel}
          onQuestionAvailability={setHasQuestion}
          onOpenBrowser={(ownerId) =>
            setBrowserRequest((previous) => ({ ownerId, sequence: (previous?.sequence ?? 0) + 1 }))
          }
        />
      )}
      <Composer
        browserLauncher={browserLauncher}
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
            compaction={{
              canCompact,
              status: currentCompactionStatus,
              message: compaction && "message" in compaction ? compaction.message : null,
              unavailableReason: snapshot?.compactionUnavailableReason ?? null,
              onCheck: compaction?.phase === "uncertain" && ready ? onRefresh : undefined,
              onCancel:
                ready && snapshot && !(compaction?.phase === "observing" && compaction.cancelling)
                  ? () => void commands.cancelCompaction(sessionId, snapshot)
                  : undefined,
              onCompact: () => {
                if (!workingDirectoryGate.current && snapshot)
                  void commands.compact(sessionId, snapshot, canCompact, defaultSelection);
              },
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
        hasImages={hasComposerImages}
        imagePreviews={
          <DraftImagePreviews
            sessionId={sessionId}
            images={chat.draftImages}
            uploads={composerUploads}
            input={composerInput}
            onRemove={(imageId) => removeDraftImage(sessionId, imageId)}
            modelError={tooManyImages ? imageLimitError : imageBlocked ? imageModelError : null}
            paused={!active}
          />
        }
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
        canSend={
          state.canSend && !sendingBlocked && !workingDirectoryLocked && !executionSettingsSaving
        }
        canContinue={
          state.canContinue &&
          !hasComposerImages &&
          !workingDirectoryLocked &&
          !executionSettingsSaving
        }
        continuationAvailable={state.continuationAvailable}
        working={state.active}
        stopping={state.executionStatus === "stopping"}
        onSend={() => {
          if (
            !workingDirectoryGate.current &&
            !sendingBlocked &&
            !composerUploads.pending() &&
            snapshot
          )
            void commands.send(sessionId, snapshot, state.active, state.canSend, defaultSelection);
        }}
        onContinue={() => {
          if (!workingDirectoryGate.current && !composerUploads.pending() && snapshot)
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
    </>
  );

  return (
    <section
      ref={panel}
      id={active ? "chat-view" : undefined}
      hidden={!active}
      data-chat-session={sessionId}
      class="chat-view"
      role="tabpanel"
      aria-labelledby={`tab-${sessionId}`}
    >
      <BrowserViewer
        sessionId={sessionId}
        active={active}
        refresh={refresh}
        browserRefresh={browserRefresh}
        openRequest={browserRequest}
        onReturnToQuestion={hasQuestion ? () => questionPanel.current?.focus() : undefined}
        onOwnersChange={setBrowserOwners}
      >
        {renderChat}
      </BrowserViewer>
    </section>
  );
}
