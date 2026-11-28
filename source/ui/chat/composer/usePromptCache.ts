import { useEffect, useState } from "preact/hooks";
import type { PromptCacheSummary } from "../../usage/Usage.ts";
import { UsageClient } from "../../usage/UsageClient.ts";

export interface PromptCacheState {
  summary: PromptCacheSummary | null;
  failed: boolean;
  refreshing: boolean;
}

export function usePromptCache(
  sessionId: string,
  active: boolean,
  refreshKey: string | number,
): PromptCacheState {
  const [state, setState] = useState<
    PromptCacheState & { sessionId: string; refreshKey: string | number }
  >({
    sessionId,
    refreshKey,
    summary: null,
    failed: false,
    refreshing: true,
  });
  useEffect(() => {
    if (!active) return;
    setState((current) => ({ ...current, refreshing: true }));
    const request = new AbortController();
    const client = new UsageClient(new URL(window.location.href));
    void client.getPromptCache(sessionId, request.signal).then(
      (summary) => {
        if (!request.signal.aborted)
          setState({ sessionId, refreshKey, summary, failed: false, refreshing: false });
      },
      () => {
        if (!request.signal.aborted)
          setState((current) => ({
            sessionId,
            refreshKey,
            refreshing: false,
            summary: current.sessionId === sessionId ? current.summary : null,
            failed: true,
          }));
      },
    );
    return () => request.abort();
  }, [sessionId, active, refreshKey]);
  if (state.sessionId !== sessionId) return { summary: null, failed: false, refreshing: true };
  return { ...state, refreshing: state.refreshing || state.refreshKey !== refreshKey };
}
