import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ModelOptionsState } from "../providers/models/useModelOptions.ts";
import { UsageOverview } from "../usage/UsageOverview.tsx";
import { ModelBucketSetup } from "./ModelBucketSetup.tsx";
import { SessionWorkingDirectory } from "./working-directory/SessionWorkingDirectory.tsx";

interface EmptyChatOverviewProps {
  client: SessionClient;
  sessionId: string;
  snapshot: SessionSnapshot;
  preferences: PreferencesState;
  ready: boolean;
  onGateChange(locked: boolean, message: string | null): void;
  onSnapshot(snapshot: SessionSnapshot): void;
  onRefresh(): void;
  active: boolean;
  visible: boolean;
  refresh: number;
  showWorkingDirectory: boolean;
  models: ModelOptionsState;
  onCompleteFocus(): void;
}

export function EmptyChatOverview({
  client,
  sessionId,
  snapshot,
  preferences,
  ready,
  onGateChange: updateWorkingDirectoryGate,
  onSnapshot: acceptSnapshot,
  onRefresh,
  active,
  visible,
  refresh,
  showWorkingDirectory,
  models,
  onCompleteFocus,
}: EmptyChatOverviewProps) {
  return (
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
          onCompleteFocus={onCompleteFocus}
        />
      )}
    </div>
  );
}
