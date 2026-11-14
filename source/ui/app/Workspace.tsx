import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Icon } from "../components/Icon.tsx";
import { Popover } from "../components/Popover.tsx";
import { Settings } from "../settings/Settings.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { ProviderClient } from "../providers/ProviderClient.ts";
import { PreferencesClient } from "../preferences/PreferencesClient.ts";
import { useWorkingDirectoryPreferences } from "../preferences/useWorkingDirectoryPreferences.ts";
import type { SettingsTab } from "../settings/Settings.tsx";
import { SessionClient } from "../sessions/SessionClient.ts";
import { MessageEditing } from "./MessageEditing.ts";
import { WorkspacePersistence } from "./WorkspacePersistence.ts";
import { emptyWorkspaceCommandState, WorkspaceCommands } from "./WorkspaceCommands.ts";
import type { WorkspaceCommandState } from "./WorkspaceCommands.ts";
import { ChatTabs } from "./ChatTabs.tsx";
import { Conversation } from "./Conversation.tsx";
import type { ReadingPosition } from "./Transcript.tsx";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import type { TranscriptNavigationState } from "../sessions/TranscriptNavigationStorage.ts";
import { usePersistedWorkspace } from "./usePersistedWorkspace.ts";
import { useWorkspaceModels } from "./useWorkspaceModels.ts";
import { useWorkspaceNavigation } from "./useWorkspaceNavigation.ts";
import { useSessionEvents } from "./useSessionEvents.ts";
import { useWorkspaceRetention } from "./useWorkspaceRetention.ts";
import { UsageControl } from "../usage/UsageControl.tsx";
import "./workspace.css";

const sessions = new SessionClient(new URL(window.location.href));
const providers = new ProviderClient(new URL(window.location.href));
const preferencesClient = new PreferencesClient(new URL(window.location.href));
const storageKey = "urd.workspace.v1";
const storageRetryLabels = { read: "Retry read", write: "Retry save", cleanup: "Retry cleanup" };

export interface WorkspaceProps {
  transcript?: TranscriptDisplay;
  transcriptError?: string | null;
}

export function Workspace({ transcript, transcriptError }: WorkspaceProps = {}) {
  const [persistence] = useState(() => new WorkspacePersistence(() => sessionStorage, storageKey));
  const workspace = usePersistedWorkspace(persistence);
  const models = useWorkspaceModels(providers);
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
      ),
  );
  const [messageEditing] = useState(
    () => new MessageEditing(sessions, workspace.access, refreshSnapshot),
  );
  useEffect(() => {
    if (workspace.loaded) return messageEditing.watchPendingReceipts();
  }, [messageEditing, workspace.loaded]);
  const create = useCallback(() => commands.create(), [commands]);
  const navigation = useWorkspaceNavigation(sessions, workspace, create);

  // Receipt reconciliation and reading positions belong to the workspace, not a mounted chat.
  useEffect(() => {
    if (workspace.loaded) return commands.watchPendingReceipts();
  }, [commands, workspace.loaded]);
  const readingPositions = useRef(new Map<string, ReadingPosition>());
  const transcriptPositions = useRef(new Map<string, TranscriptNavigationState>());
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsTab, setSettingsTab] = useState<SettingsTab>("providers");
  const workingDirectoryPreferences = useWorkingDirectoryPreferences(preferencesClient);
  const openSettings = (tab?: SettingsTab) => {
    if (tab) setSettingsTab(tab);
    void workingDirectoryPreferences.refresh();
    setSettingsOpen(true);
  };
  const settingsAnchor = useRef<HTMLButtonElement>(null);
  const [optionsOpen, setOptionsOpen] = useState(false);
  const newChatButton = useRef<HTMLButtonElement>(null);
  const { activeId, chats, tabs } = workspace.state;
  const events = useSessionEvents(
    sessions,
    workspace.access,
    navigation.discovered,
    navigation.ready,
    tabs,
    activeId,
  );
  const shown = useWorkspaceRetention(activeId, tabs, transcript?.retainedTabs ?? null);
  const canPersist = workspace.access.canPersist();
  const connected = navigation.ready && events.connected;
  const disconnected = navigation.error !== null || events.connectionState === "disconnected";
  const error =
    commandError || navigation.error || (events.connected ? events.error : null) || transcriptError;

  return (
    <main class="workspace">
      <header class="top-strip">
        <ChatTabs
          tabs={tabs.map((id) => ({
            id,
            title: chats[id]?.title ?? "Chat",
            status: events.statuses[id],
            knownStatus: events.knownStatuses[id],
          }))}
          activeId={activeId}
          unread={events.unread}
          onOpen={navigation.open}
          onClose={navigation.close}
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
              : connected
                ? "New chat"
                : "New chat unavailable while disconnected"
          }
          placement="below"
          disabled={commandState.creating || !connected || !canPersist}
          aria-busy={commandState.creating}
          onClick={() => {
            if (connected) void create();
          }}
        />
        <Tooltip
          content="Workspace options"
          placement="below"
          disabled={optionsOpen || settingsOpen}
        >
          <button
            ref={settingsAnchor}
            type="button"
            class="icon-button workspace-options-trigger"
            aria-label="Workspace options"
            aria-haspopup="dialog"
            aria-expanded={optionsOpen || settingsOpen}
            onClick={() => {
              if (settingsOpen) setSettingsOpen(false);
              else setOptionsOpen((open) => !open);
            }}
          >
            <Icon name="more" />
          </button>
        </Tooltip>
      </header>
      {optionsOpen && (
        <Popover
          anchor={settingsAnchor}
          class="workspace-options-popover"
          label="Workspace options"
          placement="below"
          onClose={() => setOptionsOpen(false)}
        >
          <div class="workspace-options-costs">
            <div class="workspace-options-cost">
              <span>Today</span>
              <UsageControl label="Today" range="today" />
            </div>
            <div class="workspace-options-cost">
              <span>Last 7 days</span>
              <UsageControl label="Last 7 days" range="sevenDays" />
            </div>
          </div>
          <div class="workspace-options-divider" />
          <button
            type="button"
            class="workspace-options-action"
            onClick={() => {
              setOptionsOpen(false);
              openSettings();
            }}
          >
            <Icon name="settings" />
            <span>Settings</span>
          </button>
          <button
            type="button"
            class="workspace-options-action is-unavailable"
            aria-disabled="true"
          >
            <Icon name="moon" />
            <span>Snooze</span>
            <span class="workspace-options-unavailable">Not available yet</span>
          </button>
        </Popover>
      )}
      <Settings
        open={settingsOpen}
        anchor={settingsAnchor}
        client={providers}
        preferences={workingDirectoryPreferences}
        tab={settingsTab}
        onTabChange={setSettingsTab}
        onClose={() => setSettingsOpen(false)}
        onChanged={models.providersChanged}
      />
      <div
        class={`connection-feedback ${disconnected ? "is-disconnected" : ""}`}
        role="status"
        hidden={connected || events.connectionState === "paused"}
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
          <Conversation
            key={id}
            client={sessions}
            sessionId={id}
            chat={chats[id]!}
            active={id === activeId}
            transcript={transcript}
            streamId={events.streamId}
            commands={commands}
            messageEditing={messageEditing}
            stop={commandState.stopping[id]}
            checking={commandState.checking[id] ?? false}
            checkingContinuation={commandState.checkingContinuations[id] ?? false}
            models={models}
            preferences={workingDirectoryPreferences}
            ready={connected}
            canPersist={canPersist}
            notice={id === activeId ? events.notice : null}
            refresh={refresh}
            onRefresh={refreshSnapshot}
            onSetUpProvider={() => openSettings("providers")}
            editDraft={workspace.editDraft}
            selectModel={workspace.selectModel}
            position={readingPositions.current.get(id)}
            onPositionChange={(position) => {
              readingPositions.current.set(id, position);
            }}
            transcriptPosition={transcriptPositions.current.get(id)}
            onTranscriptPositionChange={(position) => {
              transcriptPositions.current.set(id, position);
            }}
          />
        ))
      ) : (
        <div class="empty-workspace" />
      )}
    </main>
  );
}
