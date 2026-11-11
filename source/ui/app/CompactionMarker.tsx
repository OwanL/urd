import { useEffect, useRef, useState } from "preact/hooks";
import type { TranscriptCompactionFacts, TranscriptEntryRef } from "../sessions/Messages.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";

export interface CompactionMarkerProps {
  client: SessionClient;
  sessionId: string;
  entry: TranscriptEntryRef;
  compaction: TranscriptCompactionFacts;
}

export function CompactionMarker({ client, sessionId, entry, compaction }: CompactionMarkerProps) {
  const [expanded, setExpanded] = useState(false);
  const [summary, setSummary] = useState<string | null>(null);
  const [createdAt, setCreatedAt] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const request = useRef<AbortController | null>(null);
  const summaryId = `compaction-summary-${entry.executionId}-${entry.entryId}`;

  useEffect(() => () => request.current?.abort(), []);

  async function loadSummary() {
    if (request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true);
    setError(null);
    try {
      const result = await client.getCompactionSummary(
        sessionId,
        entry.entryId,
        entry.historyRevision,
        controller.signal,
      );
      if (
        result.id !== entry.entryId ||
        result.sessionId !== sessionId ||
        result.historyRevision !== entry.historyRevision
      )
        throw new Error("Compaction summary did not match this history entry.");
      setSummary(result.summary);
      setCreatedAt(result.createdAt);
    } catch (cause) {
      if (!controller.signal.aborted)
        setError(
          cause instanceof Error ? cause.message : "Compaction summary could not be loaded.",
        );
    } finally {
      if (request.current === controller) {
        request.current = null;
        setLoading(false);
      }
    }
  }

  function toggle() {
    if (expanded) {
      setExpanded(false);
      request.current?.abort();
      request.current = null;
      setLoading(false);
      return;
    }
    setExpanded(true);
    if (summary === null && request.current === null) void loadSummary();
  }

  return (
    <div class="message message-assistant">
      <button
        type="button"
        class="quiet-action"
        aria-expanded={expanded}
        aria-controls={expanded ? summaryId : undefined}
        onClick={toggle}
      >
        Compaction · {compaction.reason} · {compaction.tokensBefore.toLocaleString()} →{" "}
        {compaction.tokensAfter.toLocaleString()} tokens
      </button>
      {expanded && (
        <div id={summaryId} class="message-text">
          {loading && <p>Loading summary…</p>}
          {error && (
            <p class="inline-error">
              {error}{" "}
              <button type="button" onClick={() => void loadSummary()}>
                Retry read
              </button>
            </p>
          )}
          {summary !== null && (
            <>
              <p>{summary}</p>
              {createdAt && <small>{createdAt}</small>}
            </>
          )}
        </div>
      )}
    </div>
  );
}
