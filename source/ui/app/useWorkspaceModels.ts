import { useCallback, useEffect, useState } from "preact/hooks";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { WorkspaceModels } from "./WorkspaceModels.ts";
import type { ModelOption } from "./WorkspaceModels.ts";

export interface WorkspaceModelState {
  options: ModelOption[];
  error: string | null;
  refreshing: boolean;
  providersChanged(): void;
  refresh(): Promise<void>;
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}

export function useWorkspaceModels(client: ProviderClient): WorkspaceModelState {
  const [catalog] = useState(() => new WorkspaceModels(client));
  const [catalogRevision, setCatalogRevision] = useState(0);
  const [options, setOptions] = useState<ModelOption[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const providersChanged = useCallback(() => {
    catalog.invalidate();
    setCatalogRevision((value) => value + 1);
    setRefreshing(false);
    setError(null);
  }, [catalog]);

  useEffect(() => {
    const controller = new AbortController();
    void catalog.readCached(controller.signal).then((result) => {
      if (!catalog.isCurrent(result.revision)) return;
      if (result.error) {
        setError(description(result.error));
        return;
      }
      setOptions(result.options ?? []);
      setError(null);
    });
    return () => {
      controller.abort();
      catalog.invalidate();
    };
  }, [catalogRevision, catalog]);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    setError(null);
    const result = await catalog.refresh();
    if (!catalog.isCurrent(result.revision)) return;
    if (result.error) setError(description(result.error));
    else setOptions(result.options ?? []);
    setRefreshing(false);
  }, [catalog]);

  return { options, error, refreshing, providersChanged, refresh };
}
