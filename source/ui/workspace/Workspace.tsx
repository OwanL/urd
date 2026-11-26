import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { IconButton } from "../controls/IconButton.tsx";
import { Settings } from "../settings/Settings.tsx";
import { ProviderClient } from "../providers/ProviderClient.ts";
import { PreferencesClient } from "../preferences/PreferencesClient.ts";
import { preferenceSaveBlocks, usePreferences } from "../preferences/usePreferences.ts";
import type { SettingsTab } from "../settings/Settings.tsx";
import { SessionClient } from "../sessions/SessionClient.ts";
import { MessageEditing } from "../chat/message-editing/MessageEditing.ts";
import { WorkspacePersistence } from "./WorkspacePersistence.ts";
import { emptyWorkspaceCommandState, WorkspaceCommands } from "./WorkspaceCommands.ts";
import type { WorkspaceCommandState } from "./WorkspaceCommands.ts";
import { ChatTabs } from "./tabs/ChatTabs.tsx";
import { Chat } from "../chat/Chat.tsx";
import {
  SubagentNavigationContext,
  type SubagentLink,
} from "../chat/subagents/SubagentNavigation.ts";
import { SubagentView } from "../chat/subagents/SubagentView.tsx";
import type { ReadingPosition } from "../chat/transcript/Transcript.tsx";
import type { TranscriptDisplay } from "../chat/transcript/TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../chat/transcript/navigation/TranscriptNavigationStorage.ts";
import { forgetTranscriptNavigation } from "../chat/transcript/navigation/useTranscriptNavigation.ts";
import { usePersistedWorkspace } from "./usePersistedWorkspace.ts";
import { useModelOptions } from "../providers/models/useModelOptions.ts";
import { defaultModelSelection } from "./DefaultModelSelection.ts";
import { useWorkspaceNavigation } from "./useWorkspaceNavigation.ts";
import { useSessionEvents } from "./useSessionEvents.ts";
import { useWorkspaceRetention } from "./useWorkspaceRetention.ts";
import { SnoozeClient } from "./snooze/SnoozeClient.ts";
import { SnoozedChatsButton } from "./snooze/SnoozedChatsButton.tsx";
import { activeVisibleTab, rememberSnoozedChats } from "./snooze/SnoozedWorkspace.ts";
import { useSnoozedChats } from "./snooze/useSnoozedChats.ts";
import type { SetupState } from "../setup/SetupDialog.tsx";
import "./workspace.css";

const sessions = new SessionClient(new URL(window.location.href));
const snoozeClient = new SnoozeClient(new URL(window.location.href));
const loadSubagents = (rootId: string, signal: AbortSignal) =>
  sessions.getLiveSubagents(rootId, signal);
const providers = new ProviderClient(new URL(window.location.href));
const preferencesClient = new PreferencesClient(new URL(window.location.href));
const storageKey = "urd.workspace.v1";
const noTabs: readonly string[] = [];
const storageRetryLabels = { read: "Retry read", write: "Retry save", cleanup: "Retry cleanup" };

export interface WorkspaceProps {
  transcript?: TranscriptDisplay;
  transcriptError?: string | null;
  // The first chat waits for setup so it starts with the chosen defaults.
  setup?: SetupState;
}

export function Workspace({
  transcript,
  transcriptError,
  setup = "complete",
}: WorkspaceProps = {}) {
  const [persistence] = useState(
    () => new WorkspacePersistence(() => sessionStorage, storageKey, forgetTranscriptNavigation),
  );
  const workspace = usePersistedWorkspace(persistence);
  const { isClosed } = workspace;
  const models = useModelOptions(providers);
  const preferences = usePreferences(preferencesClient);
  const creatingPreferencePending = preferenceSaveBlocks(preferences.pendingFor, "create");
  const [commandError, setCommandError] = useState<string | null>(null);
  const [commandState, setCommandState] = useState<WorkspaceCommandState>(
    emptyWorkspaceCommandState,
  );
  const [refresh, setRefresh] = useState(0);
  const refreshSnapshot = useCallback(() => setRefresh((value) => value + 1), []);
  const [commands] = useState(
    () =>
      new WorkspaceCommands(
        sessions,
        workspace.access,
        setCommandState,
        setCommandError,
        refreshSnapshot,
        preferences.saveBlocks,
      ),
  );
  const [messageEditing] = useState(
    () => new MessageEditing(sessions, workspace.access, refreshSnapshot),
  );
  const create = useCallback(() => commands.create(isClosed), [commands, isClosed]);
  const setupComplete = setup === "complete";
  const snoozed = useSnoozedChats(snoozeClient, setupComplete);
  const { chats: snoozedChats, isSnoozed, refresh: refreshSnoozed } = snoozed;
  const requestSnoozeRefresh = useCallback(() => void refreshSnoozed(), [refreshSnoozed]);
  const previousSetup = useRef(setup);
  const { providersChanged } = models;
  const { refresh: refreshPreferences } = preferences;
  useEffect(() => {
    if (previousSetup.current === "required" && setupComplete) {
      providersChanged();
      void refreshPreferences();
    }
    previousSetup.current = setup;
  }, [setup, setupComplete, providersChanged, refreshPreferences]);
  const navigation = useWorkspaceNavigation(
    sessions,
    { ...workspace, loaded: setupComplete && snoozed.ready ? workspace.loaded : null },
    create,
    isSnoozed,
  );
  const access = workspace.access;
  useEffect(() => {
    if (!navigation.ready) return;
    const state = access.read();
    const next = rememberSnoozedChats(state, snoozed.chats, isClosed);
    if (next !== state) access.save(next);
  }, [access, isClosed, navigation.ready, snoozed.chats, workspace.state]);

  // Receipt reconciliation and reading positions belong to the workspace, not a mounted chat.
  useEffect(() => {
    if (!navigation.ready) return;
    const stopEditWatch = messageEditing.watchPendingReceipts();
    const stopCommandWatch = commands.watchPendingReceipts();
    return () => {
      stopEditWatch();
      stopCommandWatch();
    };
  }, [commands, messageEditing, navigation.ready]);
  const readingPositions = useRef(new Map<string, ReadingPosition>());
  const transcriptPositions = useRef(new Map<string, TranscriptNavigationState>());
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsTab, setSettingsTab] = useState<SettingsTab>("providers");
  const openSettings = (tab?: SettingsTab) => {
    if (tab) setSettingsTab(tab);
    void preferences.refresh();
    setSettingsOpen(true);
  };
  const settingsAnchor = useRef<HTMLButtonElement>(null);
  const newChatButton = useRef<HTMLButtonElement>(null);
  const { lastSelection, chats } = workspace.state;
  const tabs = useMemo(() => {
    if (!navigation.ready) return noTabs;
    const hidden = new Set(snoozedChats.map((chat) => chat.sessionId));
    return workspace.state.tabs.filter((id) => !hidden.has(id));
  }, [navigation.ready, snoozedChats, workspace.state.tabs]);
  const activeId = navigation.ready
    ? activeVisibleTab(workspace.state.tabs, workspace.state.activeId, isSnoozed)
    : null;
  const discoveringModels = models.loading || models.refreshing;
  const defaultSelection = useMemo(
    () =>
      navigation.ready
        ? defaultModelSelection(
            models.options,
            lastSelection,
            navigation.lastUsedSelection,
            discoveringModels,
          )
        : null,
    [
      discoveringModels,
      lastSelection,
      models.options,
      navigation.lastUsedSelection,
      navigation.ready,
    ],
  );
  const events = useSessionEvents(
    sessions,
    workspace.access,
    navigation.discovered,
    navigation.forget,
    isClosed,
    navigation.ready,
    tabs,
    activeId,
    requestSnoozeRefresh,
  );
  const [inspection, setInspection] = useState<{
    rootId: string;
    path: readonly SubagentLink[];
  } | null>(null);
  const inspected = inspection?.rootId === activeId ? inspection : null;
  const inspect = (rootId: string, path: readonly SubagentLink[]) => {
    if (isClosed(rootId) || snoozed.isSnoozed(rootId)) return;
    navigation.open(rootId);
    setInspection(path.length ? { rootId, path } : null);
  };
  useEffect(() => {
    if (inspection && !tabs.includes(inspection.rootId)) setInspection(null);
  }, [inspection, tabs]);
  const [uploading, setUploading] = useState<ReadonlySet<string>>(() => new Set());
  const onUploadsPendingChange = useCallback((id: string, pending: boolean) => {
    setUploading((previous) => {
      if (previous.has(id) === pending) return previous;
      const next = new Set(previous);
      if (pending) next.add(id);
      else next.delete(id);
      return next;
    });
  }, []);
  const shown = useWorkspaceRetention(activeId, tabs, transcript?.retainedTabs ?? null, uploading);
  useEffect(() => {
    for (const id of readingPositions.current.keys())
      if (isClosed(id)) readingPositions.current.delete(id);
    for (const id of transcriptPositions.current.keys())
      if (isClosed(id)) transcriptPositions.current.delete(id);
  }, [tabs, isClosed]);
  const canPersist = workspace.access.canPersist();
  const connected = navigation.ready && events.connected;
  const disconnected = navigation.error !== null || events.connectionState === "disconnected";
  const closeError = commandState.closeError;
  const error =
    (closeError && chats[closeError.sessionId] ? closeError.message : null) ||
    commandError ||
    navigation.error ||
    (events.connected ? events.error : null) ||
    transcriptError;

  return (
    <main class="workspace">
      <header class="top-strip">
        <ChatTabs
          tabs={tabs.map((id) => ({
            id,
            title: chats[id]?.title ?? "Chat",
            status: events.statuses[id],
            knownStatus: events.knownStatuses[id],
            closeAvailable: connected && commands.canClose(id, events.statuses[id]),
            snoozeAvailable:
              connected &&
              events.statuses[id] === "idle" &&
              !uploading.has(id) &&
              commands.canClose(id, "idle"),
            closing: commandState.closing.has(id),
          }))}
          activeId={activeId}
          unread={events.unread}
          onOpen={(id) => inspect(id, [])}
          loadSubagents={loadSubagents}
          onOpenSubagent={inspect}
          onClose={(id) =>
            commands.close(id, connected ? events.statuses[id] : undefined, (closedId) =>
              navigation.forget([closedId]),
            )
          }
          onSnooze={(id, until) => snoozed.change(id, { until })}
          onReorder={navigation.reorder}
          emptyFocusTarget={newChatButton}
        />
        <IconButton
          buttonRef={newChatButton}
          icon="plus"
          class="new-chat"
          label="New chat"
          hint={
            commandState.creating
              ? "Creating chat…"
              : creatingPreferencePending
                ? "Saving default subagent providers…"
                : connected
                  ? "New chat"
                  : "New chat unavailable while disconnected"
          }
          placement="below"
          disabled={commandState.creating || creatingPreferencePending || !connected || !canPersist}
          aria-busy={commandState.creating}
          onClick={() => {
            if (connected) void create();
          }}
        />
        <IconButton
          buttonRef={settingsAnchor}
          icon="settings"
          label="Settings"
          placement="below"
          aria-haspopup="dialog"
          aria-expanded={settingsOpen}
          onClick={() => {
            if (settingsOpen) setSettingsOpen(false);
            else openSettings();
          }}
        />
        <SnoozedChatsButton
          enabled={setupComplete}
          connected={connected}
          ready={snoozed.ready}
          chats={snoozed.chats}
          title={(chat) => chat.title ?? chats[chat.sessionId]?.title ?? "Chat"}
          error={snoozed.error}
          restore={(chat) => snoozed.change(chat.sessionId, { expectedUntil: chat.until })}
          retry={requestSnoozeRefresh}
        />
      </header>
      <Settings
        open={settingsOpen}
        anchor={settingsAnchor}
        client={providers}
        preferences={preferences}
        tab={settingsTab}
        onTabChange={setSettingsTab}
        onClose={() => setSettingsOpen(false)}
        onChanged={models.providersChanged}
        onProviderReady={models.providerReady}
      />
      <div
        class={`connection-feedback ${disconnected ? "is-disconnected" : ""}`}
        role="status"
        hidden={
          connected || events.connectionState === "paused" || (!setupComplete && !disconnected)
        }
      >
        <span>
          {disconnected ? "Disconnected from" : "Connecting to"} {window.location.origin}.
          {disconnected && navigation.ready ? " Reconnecting…" : ""}
        </span>
        {navigation.error && (
          <button type="button" onClick={() => window.location.reload()}>
            Retry connection
          </button>
        )}
        {!snoozed.ready && snoozed.error && (
          <button type="button" onClick={() => void snoozed.refresh()}>
            Retry loading snoozed chats
          </button>
        )}
      </div>
      {error && (
        <div class="workspace-error" role="alert">
          {error}
        </div>
      )}
      {workspace.storageIssue && (
        <div class="workspace-error" role="alert">
          {workspace.storageIssue.message}{" "}
          <button type="button" onClick={workspace.retryStorage}>
            {storageRetryLabels[workspace.storageIssue.kind]}
          </button>
        </div>
      )}
      {inspected && (
        <SubagentView
          key={inspected.path.at(-1)!.id}
          rootId={inspected.rootId}
          rootTitle={chats[inspected.rootId]?.title || "Chat"}
          path={inspected.path}
          client={sessions}
          display={transcript}
          streamId={events.streamId}
          connected={connected}
          notice={events.notice}
          onNavigate={(path) => inspect(inspected.rootId, path)}
        />
      )}
      <SubagentNavigationContext.Provider
        value={(child) => {
          if (child.parentId === activeId) inspect(activeId, [child]);
        }}
      >
        {shown.length ? (
          shown.map((id) => (
            <Chat
              key={id}
              client={sessions}
              providerClient={providers}
              sessionId={id}
              chat={chats[id]!}
              defaultSelection={defaultSelection}
              active={id === activeId && !inspected}
              pasteEnabled={!settingsOpen}
              onUploadsPendingChange={onUploadsPendingChange}
              transcript={transcript}
              streamId={events.streamId}
              commands={commands}
              messageEditing={messageEditing}
              stop={commandState.stopping[id]}
              checking={commandState.checking[id] ?? false}
              checkingContinuation={commandState.checkingContinuations[id] ?? false}
              models={models}
              preferences={preferences}
              connected={connected}
              unavailable={events.knownStatuses[id] === "unavailable"}
              closing={commandState.closing.has(id)}
              sessionStatus={events.statuses[id]}
              canPersist={canPersist}
              notice={id === activeId ? events.notice : null}
              provisioning={id === activeId ? events.provisioning : null}
              refresh={refresh}
              browserRefresh={events.browserRefresh[id] ?? 0}
              onRefresh={refreshSnapshot}
              onSetUpProvider={() => openSettings("providers")}
              onChooseModels={() => openSettings("model-buckets")}
              editDraft={workspace.editDraft}
              appendDraftImage={workspace.appendDraftImage}
              removeDraftImage={workspace.removeDraftImage}
              selectModel={workspace.selectModel}
              position={readingPositions.current.get(id)}
              onPositionChange={(position) => {
                if (!isClosed(id)) readingPositions.current.set(id, position);
              }}
              transcriptPosition={transcriptPositions.current.get(id)}
              onTranscriptPositionChange={(position) => {
                if (!isClosed(id)) transcriptPositions.current.set(id, position);
              }}
            />
          ))
        ) : (
          <div class="empty-workspace" />
        )}
      </SubagentNavigationContext.Provider>
    </main>
  );
}
