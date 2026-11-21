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
import type { SetupState } from "../setup/SetupDialog.tsx";
import "./workspace.css";

const sessions = new SessionClient(new URL(window.location.href));
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
    { ...workspace, loaded: setupComplete ? workspace.loaded : null },
    create,
  );

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
  const tabs = navigation.ready ? workspace.state.tabs : noTabs;
  const activeId = navigation.ready ? workspace.state.activeId : null;
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
  );
  const shown = useWorkspaceRetention(activeId, tabs, transcript?.retainedTabs ?? null);
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
            closing: commandState.closing.has(id),
          }))}
          activeId={activeId}
          unread={events.unread}
          onOpen={navigation.open}
          onClose={(id) =>
            commands.close(id, connected ? events.statuses[id] : undefined, (closedId) =>
              navigation.forget([closedId]),
            )
          }
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
        <IconButton
          icon="moon"
          label="Snooze"
          hint="Snooze — not available yet"
          placement="below"
          aria-disabled="true"
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
      {shown.length ? (
        shown.map((id) => (
          <Chat
            key={id}
            client={sessions}
            providerClient={providers}
            sessionId={id}
            chat={chats[id]!}
            defaultSelection={defaultSelection}
            active={id === activeId}
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
            onRefresh={refreshSnapshot}
            onSetUpProvider={() => openSettings("providers")}
            onChooseModels={() => openSettings("model-buckets")}
            editDraft={workspace.editDraft}
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
    </main>
  );
}
