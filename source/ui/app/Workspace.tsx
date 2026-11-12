import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Icon } from "../components/Icon.tsx";
import { Settings } from "../settings/Settings.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { ProviderClient } from "../providers/ProviderClient.ts";
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
import "./workspace.css";

const sessions = new SessionClient(new URL(window.location.href));
const providers = new ProviderClient(new URL(window.location.href));
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
  const settingsAnchor = useRef<HTMLButtonElement>(null);
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
  const error = commandError || navigation.error || events.error || transcriptError;

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
          hint={commandState.creating ? "Creating chat…" : "New chat"}
          placement="below"
          disabled={commandState.creating || !navigation.ready || !canPersist}
          aria-busy={commandState.creating}
          onClick={() => {
            void create();
          }}
        />
        <div class="top-actions">
          <IconButton
            icon="moon"
            label="Snooze"
            hint={
              <>
                Snooze<span class="tooltip-detail">Not available yet</span>
              </>
            }
            placement="below"
            aria-disabled="true"
          />
          <Tooltip content="Settings" placement="below" disabled={settingsOpen}>
            <button
              ref={settingsAnchor}
              type="button"
              class="icon-button"
              aria-label="Settings"
              aria-haspopup="dialog"
              aria-expanded={settingsOpen}
              onClick={() => setSettingsOpen((value) => !value)}
            >
              <Icon name="settings" />
            </button>
          </Tooltip>
        </div>
      </header>
      <Settings
        open={settingsOpen}
        anchor={settingsAnchor}
        client={providers}
        onClose={() => setSettingsOpen(false)}
        onChanged={models.providersChanged}
      />
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
            models={models}
            ready={navigation.ready}
            canPersist={canPersist}
            notice={id === activeId ? events.notice : null}
            refresh={refresh}
            onRefresh={refreshSnapshot}
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
