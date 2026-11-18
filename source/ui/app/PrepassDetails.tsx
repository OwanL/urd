import { useEffect, useRef, useState } from "preact/hooks";
import type { PrepassDetails, PrepassResource } from "../sessions/Prepass.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import { formatPrepassElapsed, modelDisplayName, prepassCostLabel } from "./PrepassValues.ts";
import "./prepass-details.css";

// Read-only preparation detail surface. It mounts only while its tooltip is open: the
// initial read and running polls abort on close, tab-hide or unmount, and accumulated
// candidate and result pages clear with the surface.
export function PrepassDetailsView({
  client,
  sessionId,
  boundaryId,
  category,
  historyRevision,
  modelNames,
  summaryError,
}: {
  client: SessionClient;
  sessionId: string;
  boundaryId: string;
  category: string;
  historyRevision: number;
  modelNames: ReadonlyMap<string, string>;
  summaryError?: string | null | undefined;
}) {
  const [hidden, setHidden] = useState(document.hidden);
  const [details, setDetails] = useState<PrepassDetails | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const candidates = useRef<PrepassResource[]>([]);
  const results = useRef<PrepassResource[]>([]);
  const offsets = useRef({ candidate: 0, result: 0 });
  const published = useRef<PrepassDetails | null>(null);
  const loadMore = useRef<(() => void) | null>(null);
  const running = details !== null && details.state === "running";

  useEffect(() => {
    const update = () => setHidden(document.hidden);
    document.addEventListener("visibilitychange", update);
    return () => document.removeEventListener("visibilitychange", update);
  }, []);

  // The elapsed label ticks locally between polls; the finish only ever arrives published.
  useEffect(() => {
    if (!running || hidden) return;
    setNow(Date.now());
    const timer = window.setInterval(() => setNow(Date.now()), 500);
    return () => window.clearInterval(timer);
  }, [running, hidden]);

  useEffect(() => {
    if (hidden) return;
    let disposed = false;
    let inFlight = false;
    let timer: number | undefined;
    let knownRunning = false;
    const controller = new AbortController();

    function append(
      current: readonly PrepassResource[],
      page: readonly PrepassResource[],
    ): PrepassResource[] {
      const known = new Set(current.map((item) => item.id));
      return [...current, ...page.filter((item) => !known.has(item.id))];
    }

    async function read() {
      if (inFlight || disposed) return;
      inFlight = true;
      try {
        const next = await client.getPrepassDetails(
          sessionId,
          {
            boundaryId,
            category,
            historyRevision,
            candidateOffset: offsets.current.candidate,
            resultOffset: offsets.current.result,
          },
          controller.signal,
        );
        if (disposed || controller.signal.aborted) return;
        offsets.current.candidate += next.candidates.length;
        offsets.current.result += next.results.length;
        candidates.current = append(candidates.current, next.candidates);
        results.current = append(results.current, next.results);
        knownRunning = next.state === "running";
        published.current = next;
        setDetails(next);
        setError(null);
        // Polling continues only while the published state runs and the surface stays open.
        if (knownRunning) schedulePoll(500);
      } catch (cause) {
        if (controller.signal.aborted || disposed) return;
        setError(cause instanceof Error ? cause.message : "Preparation details read failed.");
        if (knownRunning) schedulePoll(500);
      } finally {
        inFlight = false;
      }
    }

    function schedulePoll(delay: number) {
      if (disposed || timer !== undefined) return;
      timer = window.setTimeout(() => {
        timer = undefined;
        void read();
      }, delay);
    }

    // Scrolling the details surface pulls the next candidate/result page while it is open.
    loadMore.current = () => {
      const current = published.current;
      if (inFlight || disposed || current === null) return;
      const moreResults =
        current.resultCount !== null &&
        current.resultCount > 0 &&
        results.current.length < current.resultCount;
      if (candidates.current.length < current.candidateCount || moreResults) void read();
    };

    void read();
    return () => {
      disposed = true;
      if (timer !== undefined) window.clearTimeout(timer);
      loadMore.current = null;
      controller.abort();
    };
  }, [hidden, client, sessionId, boundaryId, category, historyRevision]);

  const candidatesList = candidates.current;
  const resultsList = results.current;
  const detailsScroll = (event: Event) => {
    const node = event.currentTarget;
    if (!(node instanceof HTMLElement)) return;
    if (node.scrollHeight - node.scrollTop - node.clientHeight > 24) return;
    loadMore.current?.();
  };

  if (details === null && error === null && !summaryError)
    return (
      <p class="prepass-details-note" role="status">
        Loading…
      </p>
    );

  const settled = Boolean(details && details.state !== "running" && details.state !== "pending");
  const model = modelDisplayName(details?.modelId ?? null, modelNames);
  const elapsed =
    details === null ? null : formatPrepassElapsed(details.startedAt, details.finishedAt, now);
  const cost =
    details === null
      ? null
      : prepassCostLabel(details.costUsd, details.costIsEstimated, details.finishedAt !== null);

  return (
    <div class="prepass-details" onScroll={detailsScroll}>
      <MetaSegments model={model} elapsed={elapsed} cost={cost} />
      {summaryError && <p class="prepass-details-error">{summaryError}</p>}
      {error && <p class="prepass-details-error">{error}</p>}
      {details?.error && details.error !== summaryError && (
        <p class="prepass-details-error">{details.error}</p>
      )}
      {details === null && error === null && <p class="prepass-details-note">Loading…</p>}
      {details && (
        <>
          {details.candidateCount > 0 && candidatesList.length > 0 && (
            <section class="prepass-details-section">
              <div class="prepass-details-label">Candidates</div>
              <ul class="prepass-details-list">
                {candidatesList.map((candidate) => (
                  <li key={candidate.id} title={candidate.path ?? candidate.id}>
                    {candidate.label}
                    {category === "instructions" && candidate.path && (
                      <span class="prepass-details-path">{candidate.path}</span>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}
          {category === "title" ? (
            <div class="prepass-details-row">
              <span class="prepass-details-label">Title</span>
              <span class="prepass-details-result">
                {details.title ?? (settled ? "—" : <PendingArc />)}
              </span>
            </div>
          ) : resultsList.length > 0 ? (
            <section class="prepass-details-section">
              <div class="prepass-details-label">Results</div>
              <ul class="prepass-details-list">
                {resultsList.map((result) => (
                  <li key={result.id} title={result.path ?? result.id}>
                    {result.label}
                    {category === "instructions" && result.path && (
                      <span class="prepass-details-path">{result.path}</span>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          ) : settled ? (
            <div class="prepass-details-row">
              <span class="prepass-details-label">Results</span>
              <span class="prepass-details-result">
                {details.resultCount === 0 ? "None selected" : "—"}
              </span>
            </div>
          ) : (
            <div class="prepass-details-row">
              <span class="prepass-details-label">Results</span>
              <span class="prepass-details-pending">
                <PendingArc /> Pending
              </span>
            </div>
          )}
          <div class="prepass-details-previews">
            {details.reasoning && (
              <section class="prepass-details-section">
                <div class="prepass-details-label">Reasoning</div>
                <pre class="prepass-details-preview">{details.reasoning}</pre>
                {details.reasoningTruncated && (
                  <p class="prepass-details-note">Reasoning truncated.</p>
                )}
              </section>
            )}
            <section class="prepass-details-section">
              <div class="prepass-details-label">{settled ? "Output preview" : "Live preview"}</div>
              <pre class="prepass-details-preview">{details.output}</pre>
              {details.outputTruncated && <p class="prepass-details-note">Output truncated.</p>}
            </section>
          </div>
        </>
      )}
    </div>
  );
}

// Meta segments separate themselves; missing values are omitted rather than invented.
function MetaSegments({
  model,
  elapsed,
  cost,
}: {
  model: string | null;
  elapsed: string | null;
  cost: string | null;
}) {
  const segments = [model, elapsed, cost].filter(
    (segment): segment is string => typeof segment === "string" && segment.length > 0,
  );
  if (segments.length === 0) return null;
  return (
    <div class="prepass-details-meta">
      {segments.map((segment, index) => (
        <span class="prepass-details-meta-segment" key={index}>
          {segment}
        </span>
      ))}
    </div>
  );
}

function PendingArc() {
  return (
    <svg
      class="prepass-details-arc"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <circle cx="8" cy="8" r="5.5" stroke-dasharray="22 12" transform="rotate(-40 8 8)" />
    </svg>
  );
}
