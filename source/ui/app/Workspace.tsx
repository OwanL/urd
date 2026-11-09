import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../components/IconButton.tsx";
import { Icon } from "../components/Icon.tsx";
import { Settings } from "../settings/Settings.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import { ProviderClient } from "../providers/ProviderClient.ts";
import { SessionClient } from "../sessions/SessionClient.ts";
import { WorkspacePersistence } from "./WorkspacePersistence.ts";
import { emptyWorkspaceCommandState, WorkspaceCommands } from "./WorkspaceCommands.ts";
import type { WorkspaceCommandState } from "./WorkspaceCommands.ts";
import { ChatTabs } from "./ChatTabs.tsx";
import { Conversation } from "./Conversation.tsx";
import type { ReadingPosition } from "./Transcript.tsx";
import { usePersistedWorkspace } from "./usePersistedWorkspace.ts";
import { useWorkspaceModels } from "./useWorkspaceModels.ts";
import { useWorkspaceNavigation } from "./useWorkspaceNavigation.ts";
import { useSessionEvents } from "./useSessionEvents.ts";
import "./workspace.css";

const sessions = new SessionClient(new URL(window.location.href));
const providers = new ProviderClient(new URL(window.location.href));
const storageKey = "urd.workspace.v1";
const storageRetryLabels = { read: "Retry read", write: "Retry save", cleanup: "Retry cleanup" };

export function Workspace() {
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
  const create = useCallback(() => commands.create(), [commands]);
  const navigation = useWorkspaceNavigation(sessions, workspace, create);

  // Receipt reconciliation and reading positions belong to the workspace, not a mounted chat.
  useEffect(() => {
    if (workspace.loaded) return commands.watchPendingReceipts();
  }, [commands, workspace.loaded]);
  const readingPositions = useRef(new Map<string, ReadingPosition>());
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
  const chat = activeId ? chats[activeId] : undefined;
  const canPersist = workspace.access.canPersist();
  const error = commandError || navigation.error || events.error;

  return (
    <main class="workspace">
      <header class="top-strip">
        <ChatTabs
          tabs={tabs.map((id) => ({
            id,
            title: chats[id]?.title ?? "Chat",
            status: events.statuses[id],
          }))}
          activeId={activeId}
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
      {activeId && chat ? (
        <Conversation
          key={activeId}
          client={sessions}
          sessionId={activeId}
          chat={chat}
          commands={commands}
          stop={commandState.stopping[activeId]}
          checking={commandState.checking[activeId] ?? false}
          models={models}
          ready={navigation.ready}
          canPersist={canPersist}
          notice={events.notice}
          refresh={refresh}
          onRefresh={refreshSnapshot}
          editDraft={workspace.editDraft}
          selectModel={workspace.selectModel}
          position={readingPositions.current.get(activeId)}
          onPositionChange={(position) => {
            readingPositions.current.set(activeId, position);
          }}
        />
      ) : (
        <div class="empty-workspace" />
      )}
    </main>
  );
}
