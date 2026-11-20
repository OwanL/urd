import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import { UsageOverview } from "../usage/UsageOverview.tsx";
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
    </div>
  );
}
