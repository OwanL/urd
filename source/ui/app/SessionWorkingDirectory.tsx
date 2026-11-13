import { useEffect, useRef, useState } from "preact/hooks";
import { WorkingDirectoryRow } from "./WorkingDirectoryRow.tsx";
import { RequestError } from "../http/RequestError.ts";
import type { WorkingDirectoryPreferenceState } from "../preferences/useWorkingDirectoryPreferences.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";

interface SessionWorkingDirectoryProps {
  client: SessionClient;
  sessionId: string;
  workingDirectory: string | null;
  preferences: WorkingDirectoryPreferenceState;
  disabled: boolean;
  onGateChange(locked: boolean, notice: string | null): void;
  onSnapshot(snapshot: SessionSnapshot): void;
  onRefresh(): void;
}

type ChangeState = "idle" | "saving" | "uncertain" | "reconciling";

export function SessionWorkingDirectory({
  client,
  sessionId,
  workingDirectory,
  preferences,
  disabled,
  onGateChange,
  onSnapshot,
  onRefresh,
}: SessionWorkingDirectoryProps) {
  const operationInProgress = useRef(false);
  const [directory, setDirectory] = useState(workingDirectory);
  const [changeState, setChangeState] = useState<ChangeState>("idle");
  const [failure, setFailure] = useState<string | null>(null);
  const [savedDirectory, setSavedDirectory] = useState<string | null>(null);

  useEffect(() => setDirectory(workingDirectory), [workingDirectory]);

  async function save(nextDirectory: string) {
    if (disabled || operationInProgress.current || changeState !== "idle") return false;
    operationInProgress.current = true;
    setChangeState("saving");
    setFailure(null);
    setSavedDirectory(null);
    onGateChange(true, "Saving working directory…");

    let confirmedDirectory: string;
    try {
      confirmedDirectory = await client.setWorkingDirectory(sessionId, nextDirectory);
    } catch (cause) {
      const message = saveFailure(cause);
      setFailure(message);
      setChangeState("uncertain");
      operationInProgress.current = false;
      onGateChange(true, `${message} Check the current directory before sending.`);
      return false;
    }

    setDirectory(confirmedDirectory);
    setSavedDirectory(confirmedDirectory);
    setChangeState("reconciling");
    onGateChange(true, "Working directory saved; refreshing session snapshot…");
    try {
      const snapshot = await client.getSnapshot(sessionId);
      const currentDirectory = snapshotDirectory(snapshot, sessionId);
      setDirectory(currentDirectory);
      onSnapshot(snapshot);
      setChangeState("idle");
      operationInProgress.current = false;
      onGateChange(false, null);
      setSavedDirectory(null);
      onRefresh();
      return true;
    } catch (cause) {
      const detail = cause instanceof Error ? cause.message : "Session read failed.";
      const message = `Working directory was saved as ${confirmedDirectory}, but the current directory could not be confirmed: ${detail}`;
      setFailure(message);
      setChangeState("uncertain");
      operationInProgress.current = false;
      onGateChange(true, `${message} Check current directory before sending.`);
      return false;
    }
  }

  async function reconcile() {
    if (operationInProgress.current || changeState !== "uncertain") return;
    operationInProgress.current = true;
    setChangeState("reconciling");
    onGateChange(true, "Checking current working directory…");

    try {
      const snapshot = await client.getSnapshot(sessionId);
      const confirmedDirectory = snapshotDirectory(snapshot, sessionId);
      setDirectory(confirmedDirectory);
      onSnapshot(snapshot);
      setChangeState("idle");
      operationInProgress.current = false;
      onGateChange(false, reconciliationMessage(failure, savedDirectory, confirmedDirectory));
      setFailure(null);
      setSavedDirectory(null);
      onRefresh();
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : "Session read failed.";
      const updatedFailure = `${failure ?? "Working directory save outcome is unknown."} Current directory could not be confirmed: ${message}`;
      setFailure(updatedFailure);
      setChangeState("uncertain");
      operationInProgress.current = false;
      onGateChange(true, `${updatedFailure} Check again before sending.`);
    }
  }

  const defaultDirectory = preferences.preferences?.workingDirectory ?? null;
  return (
    <div class="chat-working-directory">
      <WorkingDirectoryRow
        label="Default cwd"
        directory={
          preferences.loaded ? defaultDirectory : preferences.error ? "unavailable" : "loading…"
        }
        disabled={disabled || !preferences.loaded}
        busy={preferences.pending}
        onSave={preferences.save}
      />
      <WorkingDirectoryRow
        label="Current cwd"
        directory={directory}
        disabled={disabled}
        busy={changeState !== "idle"}
        onSave={save}
      />
      {changeState === "uncertain" && (
        <button type="button" onClick={() => void reconcile()}>
          Check current directory
        </button>
      )}
      {changeState === "saving" && <span>Saving current cwd…</span>}
      {changeState === "reconciling" && <span>Checking current cwd…</span>}
      {preferences.pending && <span>Saving default cwd…</span>}
      {preferences.error && <span role="alert">{preferences.error}</span>}
      {preferences.error && !preferences.loaded && (
        <button type="button" onClick={() => void preferences.refresh()}>
          Retry default cwd
        </button>
      )}
    </div>
  );
}

function snapshotDirectory(snapshot: SessionSnapshot, sessionId: string): string | null {
  if (
    snapshot.sessionId !== sessionId ||
    (snapshot.workingDirectory !== null && typeof snapshot.workingDirectory !== "string")
  )
    throw new TypeError("Server returned an invalid session snapshot.");
  return snapshot.workingDirectory;
}

function saveFailure(cause: unknown): string {
  if (cause instanceof RequestError && (cause.status === 400 || cause.status === 409)) {
    const detail = responseError(cause.responseBody);
    return `Working directory change was rejected (HTTP ${cause.status}).${detail ? ` ${detail}` : ""}`;
  }
  const detail = cause instanceof Error ? cause.message : "Request failed.";
  return `Working directory save outcome is unknown. ${detail}`;
}

function responseError(body: unknown): string | null {
  if (body === null || typeof body !== "object" || !("error" in body)) return null;
  const error = body.error;
  return typeof error === "string" && error.trim() ? error : null;
}

function reconciliationMessage(
  failure: string | null,
  savedDirectory: string | null,
  directory: string | null,
): string {
  const current = directory ?? "not set";
  if (savedDirectory !== null)
    return `Working directory was saved; current directory confirmed: ${current}.`;
  if (failure?.startsWith("Working directory change was rejected"))
    return `${failure} Current directory confirmed: ${current}.`;
  return `Working directory save outcome was unknown; current directory confirmed: ${current}.`;
}
