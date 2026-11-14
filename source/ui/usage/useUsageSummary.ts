import { useEffect, useState } from "preact/hooks";
import { UsageClient } from "./UsageClient.ts";
import type { UsageRange, UsageSummary } from "./UsageTypes.ts";
import { usageRange } from "./UsageValues.ts";

export interface UsageSummaryState {
  summary: UsageSummary | null;
  loading: boolean;
  stale: boolean;
}

export function useUsageSummary({
  client,
  enabled,
  pollMilliseconds,
  refreshKey,
  sessionId,
  range,
  detail = false,
}: {
  client: UsageClient;
  enabled: boolean;
  pollMilliseconds: number | null;
  refreshKey: number | string;
  sessionId?: string;
  range?: UsageRange;
  detail?: boolean;
}): UsageSummaryState {
  const scope = sessionId ? `session:${sessionId}` : `range:${range ?? "day"}`;
  const [state, setState] = useState<UsageSummaryState & { scope: string }>({
    scope,
    summary: null,
    loading: false,
    stale: false,
  });
  const [visible, setVisible] = useState(() => !document.hidden);
  const [focusVersion, setFocusVersion] = useState(0);

  useEffect(() => {
    function refreshVisibility() {
      const nextVisible = !document.hidden;
      setVisible(nextVisible);
      if (nextVisible) setFocusVersion((version) => version + 1);
    }
    document.addEventListener("visibilitychange", refreshVisibility);
    window.addEventListener("focus", refreshVisibility);
    return () => {
      document.removeEventListener("visibilitychange", refreshVisibility);
      window.removeEventListener("focus", refreshVisibility);
    };
  }, []);

  useEffect(() => {
    if (!enabled || !visible) return;
    let disposed = false;
    let reading = false;
    let controller: AbortController | null = null;

    async function read() {
      if (disposed || document.hidden || reading) return;
      reading = true;
      const request = new AbortController();
      controller = request;
      setState((current) =>
        current.scope === scope
          ? { ...current, loading: current.summary === null }
          : { scope, summary: null, loading: true, stale: false },
      );
      try {
        const query = sessionId
          ? { sessionId, ...(detail ? {} : { detail: false as const }) }
          : {
              ...usageRange(new Date(), range ?? "day"),
              ...(detail ? {} : { detail: false as const }),
            };
        const summary = await client.getSummary(query, request.signal);
        if (request.signal.aborted || disposed) return;
        setState({ scope, summary, loading: false, stale: false });
      } catch {
        if (!request.signal.aborted && !disposed)
          setState((current) => ({
            scope,
            summary: current.scope === scope ? current.summary : null,
            loading: false,
            stale: true,
          }));
      } finally {
        if (controller === request) controller = null;
        reading = false;
      }
    }

    void read();
    const timer =
      pollMilliseconds === null
        ? undefined
        : window.setInterval(() => void read(), pollMilliseconds);
    return () => {
      disposed = true;
      if (timer !== undefined) window.clearInterval(timer);
      controller?.abort();
    };
  }, [
    client,
    detail,
    enabled,
    focusVersion,
    pollMilliseconds,
    range,
    refreshKey,
    sessionId,
    scope,
    visible,
  ]);

  return state.scope === scope
    ? state
    : { summary: null, loading: enabled && visible, stale: false };
}
