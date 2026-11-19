import { useCallback, useEffect, useState } from "preact/hooks";
import type { ProviderClient } from "../ProviderClient.ts";
import type { ProviderConnection } from "../ProviderConnection.ts";
import { WorkspaceModels } from "./WorkspaceModels.ts";
import type { ModelOption } from "./WorkspaceModels.ts";

export interface WorkspaceModelState {
  options: ModelOption[];
  hasProviders: boolean | null;
  loading: boolean;
  refreshing: boolean;
  error: string | null;
  providersChanged(): void;
  providerReady(connectionId: string): Promise<boolean>;
  retry(): Promise<void>;
}

function description(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}

export function useWorkspaceModels(client: ProviderClient): WorkspaceModelState {
  const [catalog] = useState(() => new WorkspaceModels(client));
  const [options, setOptions] = useState<ModelOption[]>([]);
  const [hasProviders, setHasProviders] = useState<boolean | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    setError(null);
    const result = await catalog.refresh();
    if (!catalog.isCurrent(result.revision)) return [];
    setHasProviders(result.hasProviders);
    if (result.options !== null && result.providers !== null) {
      setOptions((current) =>
        replaceProviderOptions(
          current,
          result.options!,
          result.providers!,
          result.successfulProviderIds,
        ),
      );
    }
    setError(result.error === null ? null : description(result.error));
    setLoading(false);
    setRefreshing(false);
    return result.successfulProviderIds;
  }, [catalog]);

  const providersChanged = useCallback(() => {
    setHasProviders(null);
    setLoading(true);
    void refresh();
  }, [refresh]);
  const providerReady = useCallback(
    async (connectionId: string) => (await refresh()).includes(connectionId),
    [refresh],
  );
  const retry = useCallback(async () => {
    await refresh();
  }, [refresh]);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      const cached = await catalog.readCached(controller.signal);
      if (!catalog.isCurrent(cached.revision)) return;
      setHasProviders(cached.hasProviders);
      if (cached.options !== null && cached.providers !== null) {
        setOptions((current) =>
          replaceProviderOptions(
            current,
            cached.options!,
            cached.providers!,
            cached.successfulProviderIds,
          ),
        );
      }
      setLoading(false);
      setError(cached.error === null ? null : description(cached.error));

      if (cached.providersToRefresh.length === 0) {
        setRefreshing(false);
        return;
      }

      setRefreshing(true);
      const discovered = await catalog.discover(
        cached.providersToRefresh,
        cached.revision,
        controller.signal,
      );
      if (!catalog.isCurrent(discovered.revision)) return;
      if (discovered.options !== null) {
        setOptions((current) =>
          replaceProviderOptions(
            current,
            discovered.options!,
            cached.providers ?? [],
            discovered.successfulProviderIds,
          ),
        );
      }
      setError(discovered.error === null ? null : description(discovered.error));
      setRefreshing(false);
    }

    void load();
    return () => {
      controller.abort();
      catalog.invalidate();
    };
  }, [catalog]);

  return {
    options,
    hasProviders,
    loading,
    refreshing,
    error,
    providersChanged,
    providerReady,
    retry,
  };
}

export function replaceProviderOptions(
  current: readonly ModelOption[],
  replacements: readonly ModelOption[],
  providers: readonly ProviderConnection[],
  successfulProviderIds: readonly string[],
): ModelOption[] {
  const successfulIds = new Set(successfulProviderIds);
  const currentByProvider = groupOptions(current);
  const replacementByProvider = groupOptions(replacements);
  return providers.flatMap(({ id }) =>
    successfulIds.has(id)
      ? (replacementByProvider.get(id) ?? [])
      : (currentByProvider.get(id) ?? []),
  );
}

function groupOptions(options: readonly ModelOption[]): Map<string, ModelOption[]> {
  const grouped = new Map<string, ModelOption[]>();
  for (const option of options) {
    const providerId = option.selection.connection.id;
    const providerOptions = grouped.get(providerId);
    if (providerOptions) providerOptions.push(option);
    else grouped.set(providerId, [option]);
  }
  return grouped;
}
