import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../../http/RequestError.ts";
import type { SnoozeClient, SnoozedChat, SnoozedChatsSnapshot } from "./SnoozeClient.ts";

const noChats: readonly SnoozedChat[] = [];
const retryDelay = 5_000;
const maxTimeout = 2_147_483_647;

export function useSnoozedChats(client: SnoozeClient, enabled: boolean) {
  const [snapshot, setSnapshot] = useState<SnoozedChatsSnapshot | null>(null);
  const [failures, setFailures] = useState(0);
  const receivedAt = useRef(0);
  const latest = useRef(new Set<string>());
  const request = useRef<AbortController | null>(null);
  const isSnoozed = useCallback((id: string) => latest.current.has(id), []);
  const refresh = useCallback(async () => {
    if (!enabled) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    try {
      const next = await client.read(
        AbortSignal.any([controller.signal, AbortSignal.timeout(15_000)]),
      );
      if (controller.signal.aborted) return;
      receivedAt.current = performance.now();
      latest.current = new Set(next.sessions.map((chat) => chat.sessionId));
      setSnapshot(next);
      setFailures(0);
    } catch {
      if (!controller.signal.aborted) setFailures((previous) => previous + 1);
    }
  }, [client, enabled]);

  useEffect(() => {
    void refresh();
    return () => request.current?.abort();
  }, [refresh]);

  useEffect(() => {
    const next = snapshot?.sessions[0];
    if (!enabled || (!failures && !next)) return;
    // Deadlines are measured against the server's clock. Re-read rather than applying a stale
    // timer to a chat that may already have been restored and snoozed again.
    const remaining =
      next && snapshot
        ? Date.parse(next.until) -
          Date.parse(snapshot.now) -
          (performance.now() - receivedAt.current)
        : retryDelay;
    const delay = failures ? retryDelay : Math.max(1, Math.min(maxTimeout, remaining));
    const timer = window.setTimeout(() => void refresh(), delay);
    return () => window.clearTimeout(timer);
  }, [enabled, failures, refresh, snapshot]);

  const change = async (id: string, value: { until: string } | { expectedUntil: string }) => {
    try {
      await client.change(id, value);
    } catch (failure) {
      if (failure instanceof RequestError && failure.status === 409) {
        return "until" in value
          ? "Only idle chats with no pending work can be snoozed."
          : "The snooze changed. Try again.";
      }
      if (failure instanceof RequestError && failure.status === 400 && "until" in value)
        return "Choose a return time in the future.";
      return "Couldn’t confirm the change.";
    } finally {
      void refresh();
    }
    return null;
  };

  return {
    ready: snapshot !== null,
    chats: snapshot?.sessions ?? noChats,
    error: failures > 0,
    isSnoozed,
    refresh,
    change,
  };
}
