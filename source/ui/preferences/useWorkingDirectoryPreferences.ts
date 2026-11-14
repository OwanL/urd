import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import type { Preferences, PreferencesClient } from "./PreferencesClient.ts";

export interface WorkingDirectoryPreferenceState {
  preferences: Preferences | null;
  loaded: boolean;
  loading: boolean;
  pending: boolean;
  error: string | null;
  refresh(): Promise<void>;
  clearError(): void;
  save(workingDirectory: string | null): Promise<boolean>;
}

// Reads once on mount and on refresh. A save invalidates earlier reads, and reads wait
// out a pending save so its response stays authoritative.
export function useWorkingDirectoryPreferences(
  client: PreferencesClient,
): WorkingDirectoryPreferenceState {
  const [preferences, setPreferences] = useState<Preferences | null>(null);
  const [loading, setLoading] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const version = useRef(0);
  const saving = useRef(false);

  const refresh = useCallback(async () => {
    if (saving.current) return;
    const current = ++version.current;
    setLoading(true);
    try {
      const result = await client.get();
      if (current !== version.current) return;
      setPreferences(result);
      setError(null);
    } catch (cause) {
      if (current === version.current)
        setError(failureMessage(cause, "Preferences could not be loaded."));
    } finally {
      if (current === version.current) setLoading(false);
    }
  }, [client]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const save = useCallback(
    async (workingDirectory: string | null) => {
      if (saving.current) return false;
      saving.current = true;
      version.current++;
      setPending(true);
      setLoading(false);
      setError(null);
      try {
        setPreferences(await client.setWorkingDirectory(workingDirectory));
        return true;
      } catch (cause) {
        setError(failureMessage(cause, "Working directory could not be saved."));
        return false;
      } finally {
        saving.current = false;
        setPending(false);
      }
    },
    [client],
  );

  return {
    preferences,
    loaded: preferences !== null,
    loading,
    pending,
    error,
    refresh,
    save,
    clearError: () => {
      if (!saving.current && preferences !== null) setError(null);
    },
  };
}

function failureMessage(cause: unknown, fallback: string): string {
  if (!(cause instanceof RequestError)) return fallback;
  const body = cause.responseBody as { error?: unknown } | null;
  if (typeof body?.error === "string" && body.error.trim()) return body.error;
  return `Request failed (HTTP ${cause.status}).`;
}
