import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import type {
  AuxiliaryModelPurpose,
  ConfiguredModel,
  ModelBucket,
  ModelChoice,
  Preferences,
  PreferencesClient,
  ProtectedResourceCategory,
  SelectableResources,
} from "./PreferencesClient.ts";

export type PreferenceKey =
  | "workingDirectory"
  | "compactionTriggerTokenCount"
  | `modelChoice:${AuxiliaryModelPurpose}`
  | `modelBucket:${ModelBucket}`
  | `protectedResources:${ProtectedResourceCategory}`;

export interface PreferencesState {
  preferences: Preferences | null;
  loaded: boolean;
  loading: boolean;
  pending: boolean;
  pendingFor: PreferenceKey | null;
  error: string | null;
  errorFor(preference: PreferenceKey): string | null;
  refresh(): Promise<void>;
  getSelectableResources(signal?: AbortSignal): Promise<SelectableResources>;
  clearError(): void;
  saveWorkingDirectory(workingDirectory: string | null): Promise<boolean>;
  saveCompactionTriggerTokenCount(value: number | null): Promise<boolean>;
  saveModelChoice(purpose: AuxiliaryModelPurpose, choice: ModelChoice): Promise<boolean>;
  saveBucketModel(bucket: ModelBucket, model: ConfiguredModel | null): Promise<boolean>;
  saveProtectedItems(
    category: ProtectedResourceCategory,
    items: readonly string[],
  ): Promise<boolean>;
}

// Reads once on mount and on refresh. A save invalidates earlier reads, and reads wait
// out a pending save so its response stays authoritative.
export function usePreferences(client: PreferencesClient): PreferencesState {
  const [preferences, setPreferences] = useState<Preferences | null>(null);
  const [loading, setLoading] = useState(false);
  const [pendingFor, setPendingFor] = useState<PreferenceKey | null>(null);
  const [failure, setFailure] = useState<{
    preference: PreferenceKey | null;
    message: string;
  } | null>(null);
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
      setFailure(null);
    } catch (cause) {
      if (current === version.current)
        setFailure({
          preference: null,
          message: failureMessage(cause, "Preferences could not be loaded."),
        });
    } finally {
      if (current === version.current) setLoading(false);
    }
  }, [client]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const save = useCallback(
    async (preference: PreferenceKey, request: () => Promise<Preferences>, fallback: string) => {
      if (saving.current) return false;
      const focused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      saving.current = true;
      version.current++;
      setPendingFor(preference);
      setLoading(false);
      setFailure(null);
      try {
        setPreferences(await request());
        return true;
      } catch (cause) {
        setFailure({ preference, message: failureMessage(cause, fallback) });
        return false;
      } finally {
        saving.current = false;
        setPendingFor(null);
        requestAnimationFrame(() => {
          if (
            focused?.isConnected &&
            !saving.current &&
            document.activeElement === document.body &&
            focused.getClientRects().length > 0 &&
            getComputedStyle(focused).visibility === "visible"
          )
            focused.focus({ preventScroll: true });
        });
      }
    },
    [],
  );

  const saveWorkingDirectory = useCallback(
    (workingDirectory: string | null) =>
      save(
        "workingDirectory",
        () => client.setWorkingDirectory(workingDirectory),
        "Working directory could not be saved.",
      ),
    [client, save],
  );
  const saveCompactionTriggerTokenCount = useCallback(
    (value: number | null) =>
      save(
        "compactionTriggerTokenCount",
        () => client.setCompactionTriggerTokenCount(value),
        "Compaction trigger could not be saved.",
      ),
    [client, save],
  );
  const saveModelChoice = useCallback(
    (purpose: AuxiliaryModelPurpose, choice: ModelChoice) =>
      save(
        `modelChoice:${purpose}`,
        () => client.setModelChoice(purpose, choice),
        `${purpose[0]!.toUpperCase()}${purpose.slice(1)} model could not be saved.`,
      ),
    [client, save],
  );
  const saveBucketModel = useCallback(
    (bucket: ModelBucket, model: ConfiguredModel | null) =>
      save(
        `modelBucket:${bucket}`,
        () => client.setBucketModel(bucket, model),
        `${bucket[0]!.toUpperCase()}${bucket.slice(1)} model mapping could not be saved.`,
      ),
    [client, save],
  );
  const getSelectableResources = useCallback(
    (signal?: AbortSignal) => client.getSelectableResources(signal),
    [client],
  );
  const saveProtectedItems = useCallback(
    (category: ProtectedResourceCategory, items: readonly string[]) =>
      save(
        `protectedResources:${category}`,
        () => client.setProtectedItems(category, items),
        `Protected ${category} could not be saved.`,
      ),
    [client, save],
  );

  return {
    preferences,
    loaded: preferences !== null,
    loading,
    pending: pendingFor !== null,
    pendingFor,
    error: failure?.message ?? null,
    errorFor: (preference) =>
      failure && (failure.preference === null || failure.preference === preference)
        ? failure.message
        : null,
    refresh,
    getSelectableResources,
    saveWorkingDirectory,
    saveCompactionTriggerTokenCount,
    saveModelChoice,
    saveBucketModel,
    saveProtectedItems,
    clearError: () => {
      if (!saving.current && preferences !== null) setFailure(null);
    },
  };
}

function failureMessage(cause: unknown, fallback: string): string {
  if (!(cause instanceof RequestError)) return fallback;
  const body = cause.responseBody as { error?: unknown } | null;
  if (typeof body?.error === "string" && body.error.trim()) return body.error;
  return `Request failed (HTTP ${cause.status}).`;
}
