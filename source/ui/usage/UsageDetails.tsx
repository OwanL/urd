import { useMemo, useRef, useState } from "preact/hooks";
import type { UsageRange, UsageSummary } from "./UsageTypes.ts";
import { UsageClient } from "./UsageClient.ts";
import { UsageComposition } from "./UsageComposition.tsx";
import { UsageHistory } from "./UsageHistory.tsx";
import { UsageProviders } from "./UsageProviders.tsx";
import { useUsageSummary } from "./useUsageSummary.ts";
import {
  cumulativeUsageHistory,
  formatUsageCost,
  formatUsageCount,
  usageColors,
  usageProviders,
  usageSources,
} from "./UsageValues.ts";

export function UsageDetails({
  client,
  sessionId,
  range,
}: {
  client: UsageClient;
  sessionId?: string;
  range?: UsageRange;
}) {
  // The chip remains live; this inspection snapshot stays still until it is reopened.
  const snapshot = useRef<UsageSummary | null>(null);
  const state = useUsageSummary({
    client,
    enabled: snapshot.current === null,
    pollMilliseconds: null,
    refreshKey: 0,
    ...(sessionId ? { sessionId } : { range: range ?? "today" }),
    detail: true,
  });
  snapshot.current ??= state.summary;
  const summary = snapshot.current;
  return (
    <section class="usage-details" aria-label="Usage details">
      <header class="usage-row usage-heading">
        <h2>
          {sessionId ? "Session cost" : range === "sevenDays" ? "Cost (7 days)" : "Cost today"}
        </h2>
        <strong>{summary ? formatUsageCost(summary.totals) : "…"}</strong>
      </header>
      {state.stale && (
        <p class="usage-warning" role="status">
          {state.summary ? "Stale totals · refresh failed" : "Usage unavailable"}
        </p>
      )}
      {state.loading && <p class="usage-muted">Loading…</p>}
      {summary && (
        <SummaryContent
          summary={summary}
          session={!!sessionId}
          dateLabels={range === "sevenDays"}
        />
      )}
    </section>
  );
}

function SummaryContent({
  summary,
  session,
  dateLabels,
}: {
  summary: UsageSummary;
  session: boolean;
  dateLabels: boolean;
}) {
  const [selected, setSelected] = useState<number | null>(null);
  const history = useMemo(() => cumulativeUsageHistory(summary.history), [summary.history]);
  const providers = usageProviders(summary.breakdown);
  const sources = usageSources(summary.breakdown);
  const sourceColors = usageColors(sources.map((source) => source.key));
  const totals = summary.totals;
  return (
    <>
      {session && (
        <p class="usage-muted">
          {formatUsageCount(totals.requests)} requests
          {totals.activeRequests > 0 ? ` · ${formatUsageCount(totals.activeRequests)} active` : ""}
        </p>
      )}
      {session ? (
        <UsageComposition items={providers} label="Cost by provider" />
      ) : (
        <UsageHistory
          history={history}
          dateLabels={dateLabels}
          selected={selected}
          onSelect={setSelected}
        />
      )}
      <UsageProviders providers={providers} onInspect={() => setSelected(null)} />
      {session && sources.length > 1 && (
        <section class="usage-sources">
          <h3>Cost sources</h3>
          <UsageComposition items={sources} label="Cost by source" />
          <div class="usage-source-list">
            {sources.map((source) => (
              <div class="usage-row" key={source.key}>
                <span class="usage-swatch" style={{ background: sourceColors[source.key] }} />
                <span>{source.label}</span>
                <span class="usage-muted">{formatUsageCount(source.totals.requests)} req</span>
                <span>{formatUsageCost(source.totals)}</span>
              </div>
            ))}
          </div>
        </section>
      )}
      {totals.unknownRequests > 0 && <p class="usage-note">Chart shows known costs only.</p>}
    </>
  );
}
